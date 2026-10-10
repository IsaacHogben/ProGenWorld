using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Owns the GPU copy of all loaded chunk voxel data and draws it with the raymarching shaders.
///
/// Chunks are split into draw cells (16^3 voxels). Every non-empty cell is one box, shrunk to
/// its content and drawn with Graphics.RenderPrimitives (36 vertices, no mesh). Cells are
/// frustum culled and sorted per camera every frame.
/// The shaders read the voxel data straight from structured buffers:
///   _VoxelChunks : per chunk record (origin, voxel size, content bounds, neighbours)
///   _VoxelCells  : per draw cell content bounds
///   _VoxelBricks : brick table, (chunkSize/8)^3 entries per chunk slot
///   _VoxelPages  : pool of 8x8x8 voxel pages for bricks that are not uniform
///   _VoxelChunkGrid : chunk coordinate -> slot lookup for rays crossing chunks (shadows)
/// </summary>
public sealed class VoxelWorldRenderer : IDisposable
{
    public struct Settings
    {
        public int chunkSize;
        public int initialChunkCapacity;
        public int initialPageCapacity;
        public Material opaqueMaterial;
        public Material translucentMaterial;
        public Texture blockTextures;
        public NativeArray<BlockDatabase.BlockInfoUnmanaged> blockDb;
        public int layer;
        public int3 gridDims;              // chunk lookup grid size (must cover the loaded area)
    }

    // Must match VoxelChunk in VoxelRaymarchCore.hlsl
    [StructLayout(LayoutKind.Sequential)]
    struct ChunkGpu
    {
        public float4 originSize;
        public uint4 boundsMinRes;
        public uint4 boundsMaxFlags;
        public int4 neighborsA;
        public int4 neighborsB;
        public int4 lodBricks;   // x: level 1 brick table start, y: level 2 start, z: level count
    }

    struct CellCull
    {
        public float3 min;
        public float3 max;
        public uint flags;
        public int active;
    }

    struct SortItem : IComparable<SortItem>
    {
        public float key;
        public uint id;
        public int CompareTo(SortItem other) => key.CompareTo(other.key);
    }

    static readonly int3[] NeighborDirs =
    {
        new(-1, 0, 0), new(1, 0, 0),
        new(0, -1, 0), new(0, 1, 0),
        new(0, 0, -1), new(0, 0, 1),
    };

    static class Ids
    {
        public static readonly int Chunks = Shader.PropertyToID("_VoxelChunks");
        public static readonly int Cells = Shader.PropertyToID("_VoxelCells");
        public static readonly int MaxCells = Shader.PropertyToID("_VoxelMaxCells");
        public static readonly int Bricks = Shader.PropertyToID("_VoxelBricks");
        public static readonly int Pages = Shader.PropertyToID("_VoxelPages");
        public static readonly int BlockInfo = Shader.PropertyToID("_VoxelBlockInfo");
        public static readonly int DrawList = Shader.PropertyToID("_VoxelDrawList");
        public static readonly int Textures = Shader.PropertyToID("_Textures");
        public static readonly int Cull = Shader.PropertyToID("_Cull");
        public static readonly int ZTest = Shader.PropertyToID("_ZTest");
        public static readonly int Src = Shader.PropertyToID("_Src");
        public static readonly int Dst = Shader.PropertyToID("_Dst");
        public static readonly int Offset = Shader.PropertyToID("_Offset");
        public static readonly int Count = Shader.PropertyToID("_Count");
        public static readonly int Grid = Shader.PropertyToID("_VoxelChunkGrid");
        public static readonly int GridDims = Shader.PropertyToID("_VoxelGridDims");
        public static readonly int ChunkWorldSize = Shader.PropertyToID("_VoxelChunkWorldSize");
        public static readonly int LodDistances = Shader.PropertyToID("_VoxelLodDistances");
    }

    readonly Settings settings;
    readonly int maxBricksPerChunk;
    readonly int maxCellsPerChunk;

    // GPU storage
    GraphicsBuffer chunkBuffer;
    GraphicsBuffer cellBuffer;
    GraphicsBuffer brickBuffer;
    GraphicsBuffer pageBuffer;
    GraphicsBuffer blockInfoBuffer;
    GraphicsBuffer gridBuffer;
    ComputeShader copyShader;
    int copyKernel;

    // CPU mirrors / bookkeeping
    NativeArray<ChunkGpu> chunkRecords;
    NativeArray<uint2> cellBounds;   // GPU mirror, packed bounds per draw cell
    NativeArray<CellCull> cellCull;  // world space bounds per draw cell for culling
    int chunkCapacity;
    int pageCapacity;
    int highestSlot = -1;

    readonly Dictionary<int3, int> slotByCoord = new();
    readonly Stack<int> freeSlots = new();
    readonly Stack<int> freePages = new();
    List<int>[] pagesBySlot;
    readonly HashSet<int> dirtySlots = new();
    readonly List<int> dirtyList = new();

    NativeArray<int4> gridCells;
    int3 gridDims;
    readonly HashSet<int> dirtyGridCells = new();

    // Materials
    readonly Material opaqueOutside, opaqueInside, translucentOutside, translucentInside;

    // Per camera draw lists
    readonly Dictionary<Camera, CameraDraws> cameraDraws = new();
    readonly Plane[] frustumPlanes = new Plane[6];
    NativeArray<float4> planes;
    NativeList<SortItem> sortOpaque, sortTranslucent, sortShadow;
    NativeList<uint> listOpaqueOut, listOpaqueIn, listTranslucentOut, listTranslucentIn, listShadow;

    public int LoadedChunks => slotByCoord.Count;
    public int UsedPages => pageCapacity - freePages.Count;
    public int PageCapacity => pageCapacity;
    public long GpuBytes => (long)chunkCapacity * (Marshal.SizeOf<ChunkGpu>() + maxBricksPerChunk * 4 + maxCellsPerChunk * 8)
                          + (long)pageCapacity * VoxelGpuFormat.PageUints * 4;

    /// <summary>Draw cells submitted for the last rendered camera (opaque + translucent).</summary>
    public int LastDrawnCells { get; private set; }

    /// <summary>Extra distance around a box in which the camera counts as "inside".</summary>
    public float insideMargin = 1f;

    /// <summary>
    /// Camera distances (world units) where draw cells switch to detail level 1 (half
    /// resolution) and level 2 (quarter resolution). Chosen per cell on the GPU every frame.
    /// </summary>
    public void SetLodDistances(float level1Distance, float level2Distance)
    {
        Shader.SetGlobalVector(Ids.LodDistances, new Vector4(level1Distance, level2Distance, 0, 0));
    }

    /// <summary>Toggle voxel drawing (debugging / profiling).</summary>
    public bool drawEnabled = true;

    /// <summary>Render nearby voxels into the URP shadow maps (for non-voxel receivers).</summary>
    public bool castShadows = true;

    /// <summary>Cells closer than this to the camera cast into the URP shadow maps.</summary>
    public float shadowMapDistance = 32f;

    public VoxelWorldRenderer(Settings s)
    {
        settings = s;
        maxBricksPerChunk = VoxelGpuFormat.TotalBricks(s.chunkSize);
        maxCellsPerChunk = VoxelGpuFormat.MaxCells(s.chunkSize);

        copyShader = Resources.Load<ComputeShader>("VoxelBufferCopy");
        if (copyShader != null)
            copyKernel = copyShader.FindKernel("CopyUints");

        chunkCapacity = Mathf.Max(64, s.initialChunkCapacity);
        pageCapacity = Mathf.Max(1024, s.initialPageCapacity);

        chunkRecords = new NativeArray<ChunkGpu>(chunkCapacity, Allocator.Persistent);
        cellBounds = new NativeArray<uint2>(chunkCapacity * maxCellsPerChunk, Allocator.Persistent);
        cellCull = new NativeArray<CellCull>(chunkCapacity * maxCellsPerChunk, Allocator.Persistent);
        pagesBySlot = new List<int>[chunkCapacity];

        chunkBuffer = CreateBuffer(chunkCapacity, Marshal.SizeOf<ChunkGpu>());
        cellBuffer = CreateBuffer(chunkCapacity * maxCellsPerChunk, 8);
        brickBuffer = CreateBuffer(chunkCapacity * maxBricksPerChunk, 4);
        pageBuffer = CreateBuffer(pageCapacity * VoxelGpuFormat.PageUints, 4);

        for (int i = chunkCapacity - 1; i >= 0; i--) freeSlots.Push(i);
        for (int i = pageCapacity - 1; i >= 0; i--) freePages.Push(i);

        // Block info: visibility | alpha mask index << 8
        blockInfoBuffer = CreateBuffer(256, 4);
        var info = new NativeArray<uint>(256, Allocator.Temp);
        for (int i = 0; i < 256; i++)
            info[i] = (uint)BlockVisibility.Invisible;
        for (int i = 0; i < s.blockDb.Length && i < 256; i++)
            info[i] = (uint)s.blockDb[i].visibility | ((uint)s.blockDb[i].alphaMaskIndex << 8);
        blockInfoBuffer.SetData(info);
        info.Dispose();

        // Chunk lookup grid (toroidal, indexed by chunk coordinate) for rays that cross chunks.
        // Power of two sizes so the shader can wrap coordinates with a bitmask.
        gridDims = new int3(
            Mathf.NextPowerOfTwo(Mathf.Max(4, s.gridDims.x)),
            Mathf.NextPowerOfTwo(Mathf.Max(4, s.gridDims.y)),
            Mathf.NextPowerOfTwo(Mathf.Max(4, s.gridDims.z)));
        int gridCount = gridDims.x * gridDims.y * gridDims.z;
        gridCells = new NativeArray<int4>(gridCount, Allocator.Persistent);
        for (int i = 0; i < gridCount; i++)
            gridCells[i] = new int4(-1, 0, 0, 0);
        gridBuffer = CreateBuffer(gridCount, 16);
        gridBuffer.SetData(gridCells);

        Shader.SetGlobalVector(Ids.GridDims, new Vector4(gridDims.x, gridDims.y, gridDims.z, 0));
        Shader.SetGlobalFloat(Ids.ChunkWorldSize, s.chunkSize);
        Shader.SetGlobalFloat(Ids.MaxCells, maxCellsPerChunk);

        BindGlobals();

        opaqueOutside = new Material(s.opaqueMaterial) { name = "VoxelOpaque (outside)" };
        opaqueInside = new Material(s.opaqueMaterial) { name = "VoxelOpaque (inside)" };
        opaqueInside.EnableKeyword("_VOXEL_INSIDE");
        opaqueInside.SetFloat(Ids.Cull, (float)CullMode.Front);

        translucentOutside = new Material(s.translucentMaterial) { name = "VoxelTranslucent (outside)" };
        translucentInside = new Material(s.translucentMaterial) { name = "VoxelTranslucent (inside)" };
        translucentInside.SetFloat(Ids.Cull, (float)CullMode.Front);
        translucentInside.SetFloat(Ids.ZTest, (float)CompareFunction.Always);
        translucentInside.renderQueue = translucentOutside.renderQueue + 1;

        if (s.blockTextures != null)
        {
            foreach (var m in new[] { opaqueOutside, opaqueInside, translucentOutside, translucentInside })
                m.SetTexture(Ids.Textures, s.blockTextures);
        }

        planes = new NativeArray<float4>(6, Allocator.Persistent);
        sortOpaque = new NativeList<SortItem>(4096, Allocator.Persistent);
        sortTranslucent = new NativeList<SortItem>(256, Allocator.Persistent);
        sortShadow = new NativeList<SortItem>(1024, Allocator.Persistent);
        listOpaqueOut = new NativeList<uint>(4096, Allocator.Persistent);
        listOpaqueIn = new NativeList<uint>(16, Allocator.Persistent);
        listTranslucentOut = new NativeList<uint>(256, Allocator.Persistent);
        listTranslucentIn = new NativeList<uint>(16, Allocator.Persistent);
        listShadow = new NativeList<uint>(1024, Allocator.Persistent);

        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    // ==================================================================================
    // Chunk data
    // ==================================================================================

    public bool HasChunk(int3 coord) => slotByCoord.ContainsKey(coord);

    /// <summary>Uploads (or replaces) a chunk's voxel data. The result's brick table is modified in place.</summary>
    public void SetChunk(int3 coord, VoxelPackResult result)
    {
        if (result.IsEmpty)
        {
            RemoveChunk(coord);
            return;
        }

        bool isNew = !slotByCoord.TryGetValue(coord, out int slot);
        if (isNew)
        {
            if (freeSlots.Count == 0)
                GrowChunkCapacity();
            slot = freeSlots.Pop();
            slotByCoord[coord] = slot;
            highestSlot = Mathf.Max(highestSlot, slot);

            int g = GridIndex(coord);
            gridCells[g] = new int4(slot, coord.x, coord.y, coord.z);
            dirtyGridCells.Add(g);
        }

        var pages = pagesBySlot[slot] ??= new List<int>(64);
        ReleasePages(pages);

        int mixed = result.MixedBrickCount;
        if (freePages.Count < mixed)
            GrowPageCapacity(UsedPages + mixed);
        for (int i = 0; i < mixed; i++)
            pages.Add(freePages.Pop());

        // Brick table: replace local page indices with pool indices
        int brickCount = result.BrickCount;
        var bricks = result.bricks;
        for (int b = 0; b < brickCount; b++)
        {
            uint e = bricks[b];
            if ((e & VoxelGpuFormat.UniformFlag) == 0)
                bricks[b] = (uint)pages[(int)(e & VoxelGpuFormat.PageMask)] | (e & VoxelGpuFormat.DenseFlag);
        }
        brickBuffer.SetData(bricks, 0, slot * maxBricksPerChunk, brickCount);

        // Pages: upload consecutive runs in one call
        var pageData = result.pages.AsArray();
        for (int i = 0; i < mixed;)
        {
            int start = i;
            while (i + 1 < mixed && pages[i + 1] == pages[i] + 1)
                i++;
            int run = i - start + 1;
            pageBuffer.SetData(pageData, start * VoxelGpuFormat.PageUints, pages[start] * VoxelGpuFormat.PageUints, run * VoxelGpuFormat.PageUints);
            i++;
        }

        // Chunk record
        int res = result.Resolution;
        float voxelSize = settings.chunkSize / (float)res;
        float3 origin = (float3)(coord * settings.chunkSize);
        uint flags = result.Flags;

        var rec = chunkRecords[slot];
        rec.originSize = new float4(origin, voxelSize);
        rec.boundsMinRes = new uint4((uint3)result.BoundsMin, (uint)res);
        rec.boundsMaxFlags = new uint4((uint3)result.BoundsMax, flags);
        rec.neighborsB.z = slot * maxBricksPerChunk;

        // Coarser detail levels follow level 0 in the slot's brick table
        int levels = result.LodLevels;
        int level1 = rec.neighborsB.z + VoxelGpuFormat.BricksInLevel(res, 0);
        int level2 = levels > 1 ? level1 + VoxelGpuFormat.BricksInLevel(res, 1) : level1;
        rec.lodBricks = new int4(level1, level2, levels, 0);

        for (int f = 0; f < 6; f++)
        {
            int n = slotByCoord.TryGetValue(coord + NeighborDirs[f], out int ns) ? ns : -1;
            SetNeighbor(ref rec, f, n);

            if (isNew && n >= 0)
            {
                var nrec = chunkRecords[n];
                SetNeighbor(ref nrec, f ^ 1, slot);
                chunkRecords[n] = nrec;
                MarkDirty(n);
            }
        }
        chunkRecords[slot] = rec;
        MarkDirty(slot);

        // Draw cells
        int baseCell = slot * maxCellsPerChunk;
        int cellCount = result.CellCount;
        for (int c = 0; c < maxCellsPerChunk; c++)
        {
            if (c >= cellCount || result.CellEmpty(c))
            {
                cellBounds[baseCell + c] = default;
                cellCull[baseCell + c] = default;
                continue;
            }

            int3 cmin = result.CellMin(c);
            int3 cmax = result.CellMax(c);
            cellBounds[baseCell + c] = new uint2(
                (uint)cmin.x | ((uint)cmin.y << 8) | ((uint)cmin.z << 16),
                (uint)cmax.x | ((uint)cmax.y << 8) | ((uint)cmax.z << 16));
            cellCull[baseCell + c] = new CellCull
            {
                min = origin + (float3)cmin * voxelSize,
                max = origin + (float3)cmax * voxelSize,
                flags = result.CellFlags(c),
                active = 1
            };
        }
        cellBuffer.SetData(cellBounds, baseCell, baseCell, maxCellsPerChunk);
    }

    public void RemoveChunk(int3 coord)
    {
        if (!slotByCoord.TryGetValue(coord, out int slot))
            return;

        slotByCoord.Remove(coord);
        if (pagesBySlot[slot] != null)
            ReleasePages(pagesBySlot[slot]);

        for (int f = 0; f < 6; f++)
        {
            if (!slotByCoord.TryGetValue(coord + NeighborDirs[f], out int n))
                continue;
            var nrec = chunkRecords[n];
            SetNeighbor(ref nrec, f ^ 1, -1);
            chunkRecords[n] = nrec;
            MarkDirty(n);
        }

        int g = GridIndex(coord);
        if (gridCells[g].x == slot)
        {
            gridCells[g] = new int4(-1, 0, 0, 0);
            dirtyGridCells.Add(g);
        }

        int baseCell = slot * maxCellsPerChunk;
        for (int c = 0; c < maxCellsPerChunk; c++)
            cellCull[baseCell + c] = default;

        chunkRecords[slot] = default;
        dirtySlots.Remove(slot);
        freeSlots.Push(slot);
    }

    /// <summary>Pushes modified chunk records and grid cells to the GPU. Call once per frame before rendering.</summary>
    public void FlushUploads()
    {
        UploadDirty(dirtySlots, chunkBuffer, chunkRecords);
        UploadDirty(dirtyGridCells, gridBuffer, gridCells);
    }

    void UploadDirty<T>(HashSet<int> dirty, GraphicsBuffer buffer, NativeArray<T> data) where T : struct
    {
        if (dirty.Count == 0)
            return;

        dirtyList.Clear();
        dirtyList.AddRange(dirty);
        dirtyList.Sort();
        dirty.Clear();

        for (int i = 0; i < dirtyList.Count;)
        {
            int start = dirtyList[i];
            int end = start;
            while (i + 1 < dirtyList.Count && dirtyList[i + 1] == end + 1)
            {
                i++;
                end++;
            }
            buffer.SetData(data, start, start, end - start + 1);
            i++;
        }
    }

    int GridIndex(int3 coord)
    {
        int3 w = coord & (gridDims - 1);
        return w.x + (w.y + w.z * gridDims.y) * gridDims.x;
    }

    static void SetNeighbor(ref ChunkGpu rec, int face, int slot)
    {
        switch (face)
        {
            case 0: rec.neighborsA.x = slot; break;
            case 1: rec.neighborsA.y = slot; break;
            case 2: rec.neighborsA.z = slot; break;
            case 3: rec.neighborsA.w = slot; break;
            case 4: rec.neighborsB.x = slot; break;
            case 5: rec.neighborsB.y = slot; break;
        }
    }

    void MarkDirty(int slot) => dirtySlots.Add(slot);

    void ReleasePages(List<int> pages)
    {
        // Push in reverse so later allocations come out in ascending (contiguous) order.
        for (int i = pages.Count - 1; i >= 0; i--)
            freePages.Push(pages[i]);
        pages.Clear();
    }

    // ==================================================================================
    // Capacity growth
    // ==================================================================================

    static GraphicsBuffer CreateBuffer(int count, int stride)
    {
        return new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, count), stride);
    }

    void BindGlobals()
    {
        Shader.SetGlobalBuffer(Ids.Grid, gridBuffer);
        Shader.SetGlobalBuffer(Ids.Chunks, chunkBuffer);
        Shader.SetGlobalBuffer(Ids.Cells, cellBuffer);
        Shader.SetGlobalBuffer(Ids.Bricks, brickBuffer);
        Shader.SetGlobalBuffer(Ids.Pages, pageBuffer);
        Shader.SetGlobalBuffer(Ids.BlockInfo, blockInfoBuffer);
    }

    static NativeArray<T> Grow<T>(NativeArray<T> old, int newLength) where T : struct
    {
        var grown = new NativeArray<T>(newLength, Allocator.Persistent);
        NativeArray<T>.Copy(old, grown, old.Length);
        old.Dispose();
        return grown;
    }

    void GrowChunkCapacity()
    {
        int oldCapacity = chunkCapacity;
        int newCapacity = oldCapacity * 2;

        chunkRecords = Grow(chunkRecords, newCapacity);
        cellBounds = Grow(cellBounds, newCapacity * maxCellsPerChunk);
        cellCull = Grow(cellCull, newCapacity * maxCellsPerChunk);
        Array.Resize(ref pagesBySlot, newCapacity);

        chunkBuffer.Release();
        chunkBuffer = CreateBuffer(newCapacity, Marshal.SizeOf<ChunkGpu>());
        chunkBuffer.SetData(chunkRecords);

        cellBuffer.Release();
        cellBuffer = CreateBuffer(newCapacity * maxCellsPerChunk, 8);
        cellBuffer.SetData(cellBounds);

        brickBuffer = GrowUintBuffer(brickBuffer, oldCapacity * maxBricksPerChunk, newCapacity * maxBricksPerChunk);

        for (int i = newCapacity - 1; i >= oldCapacity; i--)
            freeSlots.Push(i);

        chunkCapacity = newCapacity;
        BindGlobals();
        Debug.Log($"[VoxelWorldRenderer] Chunk capacity grown to {newCapacity}");
    }

    void GrowPageCapacity(int required)
    {
        int oldCapacity = pageCapacity;
        int newCapacity = oldCapacity;
        while (newCapacity < required)
            newCapacity *= 2;

        pageBuffer = GrowUintBuffer(pageBuffer, oldCapacity * VoxelGpuFormat.PageUints, newCapacity * VoxelGpuFormat.PageUints);

        for (int i = newCapacity - 1; i >= oldCapacity; i--)
            freePages.Push(i);

        pageCapacity = newCapacity;
        BindGlobals();
        Debug.Log($"[VoxelWorldRenderer] Page capacity grown to {newCapacity} ({newCapacity * VoxelGpuFormat.PageUints * 4L / (1024 * 1024)} MB)");
    }

    GraphicsBuffer GrowUintBuffer(GraphicsBuffer old, int oldCount, int newCount)
    {
        var grown = CreateBuffer(newCount, 4);

        if (copyShader != null)
        {
            copyShader.SetBuffer(copyKernel, Ids.Src, old);
            copyShader.SetBuffer(copyKernel, Ids.Dst, grown);
            const int maxPerDispatch = 65535 * 256;
            for (int offset = 0; offset < oldCount; offset += maxPerDispatch)
            {
                int count = Mathf.Min(maxPerDispatch, oldCount - offset);
                copyShader.SetInt(Ids.Offset, offset);
                copyShader.SetInt(Ids.Count, count);
                copyShader.Dispatch(copyKernel, Mathf.CeilToInt(count / 256f), 1, 1);
            }
        }
        else
        {
            // Fallback: round trip through the CPU (slow, but only happens on growth).
            var tmp = new uint[oldCount];
            old.GetData(tmp);
            grown.SetData(tmp);
        }

        old.Release();
        return grown;
    }

    // ==================================================================================
    // Rendering
    // ==================================================================================

    sealed class DrawList : IDisposable
    {
        public GraphicsBuffer buffer;
        public readonly MaterialPropertyBlock props = new();
        public int count;

        public void Upload(NativeList<uint> ids)
        {
            count = ids.Length;
            if (count == 0)
                return;

            if (buffer == null || buffer.count < count)
            {
                buffer?.Release();
                buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(Mathf.Max(64, count)), 4);
                props.SetBuffer(Ids.DrawList, buffer);
            }
            buffer.SetData(ids.AsArray(), 0, 0, count);
        }

        public void Dispose() => buffer?.Release();
    }

    sealed class CameraDraws : IDisposable
    {
        public readonly DrawList opaqueOut = new(), opaqueIn = new(), translucentOut = new(), translucentIn = new(), shadow = new();
        public void Dispose()
        {
            opaqueOut.Dispose(); opaqueIn.Dispose(); translucentOut.Dispose(); translucentIn.Dispose(); shadow.Dispose();
        }
    }

    [BurstCompile]
    struct CullJob : IJob
    {
        [ReadOnly] public NativeArray<CellCull> cells;
        [ReadOnly] public NativeArray<float4> planes;
        public int count;
        public float3 cameraPos;
        public float insideMargin;
        public float3 lightDir;        // direction the main light travels
        public float shadowDistance;

        public NativeList<SortItem> sortOpaque, sortTranslucent, sortShadow;
        public NativeList<uint> opaqueOut, opaqueIn, translucentOut, translucentIn, shadow;

        public void Execute()
        {
            sortOpaque.Clear(); sortTranslucent.Clear(); sortShadow.Clear();
            opaqueOut.Clear(); opaqueIn.Clear(); translucentOut.Clear(); translucentIn.Clear(); shadow.Clear();

            for (int i = 0; i < count; i++)
            {
                var c = cells[i];
                if (c.active == 0)
                    continue;

                bool opaque = (c.flags & VoxelGpuFormat.FlagHasOpaque) != 0;
                bool translucent = (c.flags & VoxelGpuFormat.FlagHasTranslucent) != 0;
                float dist = math.distance(cameraPos, math.clamp(cameraPos, c.min, c.max));

                // Shadow casters can be outside the view frustum. Sorted front to back from the
                // light so early depth testing rejects casters hidden behind closer ones.
                if (opaque && dist <= shadowDistance)
                    sortShadow.Add(new SortItem { key = math.dot((c.min + c.max) * 0.5f, lightDir), id = (uint)i });

                bool inside = math.all(cameraPos >= c.min - insideMargin) && math.all(cameraPos <= c.max + insideMargin);
                if (!inside && !InFrustum(c.min, c.max))
                    continue;

                if (opaque)
                {
                    if (inside) opaqueIn.Add((uint)i);
                    else sortOpaque.Add(new SortItem { key = dist, id = (uint)i });
                }
                if (translucent)
                {
                    if (inside) translucentIn.Add((uint)i);
                    else sortTranslucent.Add(new SortItem { key = -math.distance(cameraPos, (c.min + c.max) * 0.5f), id = (uint)i });
                }
            }

            sortOpaque.Sort();       // front to back: early depth rejection
            sortTranslucent.Sort();  // back to front: correct blending
            sortShadow.Sort();
            for (int i = 0; i < sortOpaque.Length; i++) opaqueOut.Add(sortOpaque[i].id);
            for (int i = 0; i < sortTranslucent.Length; i++) translucentOut.Add(sortTranslucent[i].id);
            for (int i = 0; i < sortShadow.Length; i++) shadow.Add(sortShadow[i].id);
        }

        bool InFrustum(float3 min, float3 max)
        {
            float3 center = (min + max) * 0.5f;
            float3 extents = (max - min) * 0.5f;
            for (int p = 0; p < 6; p++)
            {
                float4 pl = planes[p];
                float r = math.dot(extents, math.abs(pl.xyz));
                if (math.dot(pl.xyz, center) + pl.w < -r)
                    return false;
            }
            return true;
        }
    }

    void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView)
            return;
        if (!drawEnabled || slotByCoord.Count == 0)
            return;

        FlushUploads();

        GeometryUtility.CalculateFrustumPlanes(cam, frustumPlanes);
        for (int i = 0; i < 6; i++)
            planes[i] = new float4(frustumPlanes[i].normal, frustumPlanes[i].distance);

        var job = new CullJob
        {
            cells = cellCull,
            planes = planes,
            count = (highestSlot + 1) * maxCellsPerChunk,
            cameraPos = cam.transform.position,
            insideMargin = insideMargin + cam.nearClipPlane * 2f,
            lightDir = MainLightDirection(),
            shadowDistance = Mathf.Min(shadowMapDistance, ShadowDistance(cam)),
            sortOpaque = sortOpaque,
            sortTranslucent = sortTranslucent,
            sortShadow = sortShadow,
            opaqueOut = listOpaqueOut,
            opaqueIn = listOpaqueIn,
            translucentOut = listTranslucentOut,
            translucentIn = listTranslucentIn,
            shadow = listShadow
        };
        job.Run();

        if (!cameraDraws.TryGetValue(cam, out var draws))
        {
            draws = new CameraDraws();
            cameraDraws[cam] = draws;
        }

        draws.opaqueOut.Upload(listOpaqueOut);
        draws.opaqueIn.Upload(listOpaqueIn);
        draws.translucentOut.Upload(listTranslucentOut);
        draws.translucentIn.Upload(listTranslucentIn);
        draws.shadow.Upload(listShadow);
        LastDrawnCells = listOpaqueOut.Length + listOpaqueIn.Length + listTranslucentOut.Length + listTranslucentIn.Length;

        var bounds = new Bounds(cam.transform.position, Vector3.one * 100000f);

        Draw(cam, bounds, opaqueOutside, draws.opaqueOut, ShadowCastingMode.Off);
        Draw(cam, bounds, opaqueInside, draws.opaqueIn, ShadowCastingMode.Off);
        if (castShadows)
            Draw(cam, bounds, opaqueOutside, draws.shadow, ShadowCastingMode.ShadowsOnly);
        Draw(cam, bounds, translucentOutside, draws.translucentOut, ShadowCastingMode.Off);
        Draw(cam, bounds, translucentInside, draws.translucentIn, ShadowCastingMode.Off);
    }

    Light cachedSun;

    float3 MainLightDirection()
    {
        var sun = RenderSettings.sun;
        if (sun == null)
        {
            if (cachedSun == null || !cachedSun.isActiveAndEnabled)
            {
                cachedSun = null;
                foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional && l.isActiveAndEnabled) { cachedSun = l; break; }
            }
            sun = cachedSun;
        }
        return sun != null ? (float3)sun.transform.forward : new float3(0, -1, 0);
    }

    static float ShadowDistance(Camera cam)
    {
        var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        float d = urp != null ? urp.shadowDistance : QualitySettings.shadowDistance;
        return Mathf.Min(d, cam.farClipPlane);
    }

    void Draw(Camera cam, Bounds bounds, Material material, DrawList list, ShadowCastingMode shadows)
    {
        if (list.count == 0)
            return;

        var rp = new RenderParams(material)
        {
            camera = cam,
            layer = settings.layer,
            worldBounds = bounds,
            matProps = list.props,
            shadowCastingMode = shadows,
            receiveShadows = true,
            lightProbeUsage = LightProbeUsage.BlendProbes,
            reflectionProbeUsage = ReflectionProbeUsage.Off,
            motionVectorMode = MotionVectorGenerationMode.Camera,
        };
        Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 36, list.count);
    }

    public void Dispose()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;

        // Tells receiver shaders (ProGenWorld/Lit (Voxel Shadows)) there is no voxel data any more.
        Shader.SetGlobalFloat(Ids.ChunkWorldSize, 0f);

        foreach (var d in cameraDraws.Values) d.Dispose();
        cameraDraws.Clear();

        chunkBuffer?.Release();
        cellBuffer?.Release();
        brickBuffer?.Release();
        pageBuffer?.Release();
        blockInfoBuffer?.Release();
        gridBuffer?.Release();

        if (gridCells.IsCreated) gridCells.Dispose();
        if (chunkRecords.IsCreated) chunkRecords.Dispose();
        if (cellBounds.IsCreated) cellBounds.Dispose();
        if (cellCull.IsCreated) cellCull.Dispose();
        if (planes.IsCreated) planes.Dispose();
        if (sortOpaque.IsCreated) sortOpaque.Dispose();
        if (sortTranslucent.IsCreated) sortTranslucent.Dispose();
        if (sortShadow.IsCreated) sortShadow.Dispose();
        if (listOpaqueOut.IsCreated) listOpaqueOut.Dispose();
        if (listOpaqueIn.IsCreated) listOpaqueIn.Dispose();
        if (listTranslucentOut.IsCreated) listTranslucentOut.Dispose();
        if (listTranslucentIn.IsCreated) listTranslucentIn.Dispose();
        if (listShadow.IsCreated) listShadow.Dispose();

        UnityEngine.Object.Destroy(opaqueOutside);
        UnityEngine.Object.Destroy(opaqueInside);
        UnityEngine.Object.Destroy(translucentOutside);
        UnityEngine.Object.Destroy(translucentInside);
    }
}

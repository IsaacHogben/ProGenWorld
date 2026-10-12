using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// GPU layout shared with VoxelRaymarchCore.hlsl.
/// A chunk is split into 8x8x8 bricks. A brick whose voxels are all the same block is stored
/// inline in the brick table (UniformFlag | blockId). Any other brick gets a page of 512
/// voxels (1 byte each, 128 uints) in a shared page pool.
/// Every chunk is stored at up to three detail levels (full, 1/2, 1/4 resolution); the shader
/// picks a level per draw cell from the camera distance, so LOD changes never re-upload.
/// For drawing, a chunk is also split into draw cells (16^3 voxels); each non-empty cell is
/// drawn as its own box shrunk to its content, so rays only cross small, dense regions.
/// </summary>
public static class VoxelGpuFormat
{
    public const int BrickSize = 8;
    public const int BrickVoxels = BrickSize * BrickSize * BrickSize;
    public const int PageUints = BrickVoxels / 4;
    public const uint UniformFlag = 0x80000000u;
    public const uint DenseFlag = 0x40000000u;   // mixed brick, at least half solid (coarse shadow LOD)
    public const uint PageMask = 0x3FFFFFFFu;

    // Block id written into the air voxel above a block with a grass surface decoration. Invisible
    // to everything but the shader's grass (VoxelRelief.hlsl); it makes the voxels above grass
    // individually addressable so the march can stop there. Never a real block id.
    public const byte GrassMarker = 255;

    public const uint FlagHasOpaque = 1u;      // contains opaque or alpha masked blocks
    public const uint FlagHasTranslucent = 2u; // contains translucent blocks (water...)

    public const int MaxLodLevels = 3;         // matches LODLevel Near / Mid / Far

    // Meta array layout written by VoxelPackJob
    public const int MetaMin = 0;         // 3 ints
    public const int MetaMax = 3;         // 3 ints, exclusive
    public const int MetaFlags = 6;
    public const int MetaMixedBricks = 7;
    public const int MetaResolution = 8;  // full resolution (level 0)
    public const int MetaCellsPerAxis = 9;
    public const int MetaLodLevels = 10;
    public const int MetaTotalBricks = 11;
    public const int MetaLength = 12;

    public const int CellSize = 16;      // draw cell edge in voxels (multiple of BrickSize)
    public const int CellInts = 8;       // per cell: min xyz, max xyz (exclusive), flags, occlusion

    // Cell occlusion bits (cell int 7). A cell is buried, and never drawn, when it is fully
    // opaque and the neighbouring cells' facing layers are opaque at every detail level.
    public const int OccFaceMask = 0x3F;       // bit f: the cell's boundary layer on face f is all opaque (-x,+x,-y,+y,-z,+z)
    public const int OccFull = 0x40;           // every voxel of the cell is opaque

    public static int Resolution(int chunkSize) => math.max(BrickSize, chunkSize);

    /// <summary>Detail levels a chunk of this resolution has (each level must be at least one brick wide).</summary>
    public static int LodLevels(int resolution)
    {
        int levels = 1;
        while (levels < MaxLodLevels && (resolution >> levels) >= BrickSize)
            levels++;
        return levels;
    }

    public static int BricksInLevel(int resolution, int level)
    {
        int bpa = (resolution >> level) / BrickSize;
        return bpa * bpa * bpa;
    }

    public static int TotalBricks(int chunkSize)
    {
        int res = Resolution(chunkSize);
        int total = 0;
        for (int l = 0; l < LodLevels(res); l++)
            total += BricksInLevel(res, l);
        return total;
    }

    public static int CellsPerAxis(int resolution) => math.max(1, resolution / CellSize);
    public static int MaxCells(int chunkSize)
    {
        int cpa = CellsPerAxis(Resolution(chunkSize));
        return cpa * cpa * cpa;
    }
}

/// <summary>Output of a VoxelPackJob. Pooled and reused by VoxelPackSystem.</summary>
public struct VoxelPackResult : IDisposable
{
    public NativeArray<uint> bricks;             // brick tables of all levels, level 0 first
    public NativeList<uint> pages;               // PageUints per mixed brick, in brick order
    public NativeArray<int> meta;                // see VoxelGpuFormat.Meta*
    public NativeArray<int> cells;               // CellInts per draw cell
    public NativeList<ECSSpawnPoint> ecsSpawns;  // decoration spawns (Near LOD only)
    public LODLevel lod;

    public int Resolution => meta[VoxelGpuFormat.MetaResolution];
    public int LodLevels => meta[VoxelGpuFormat.MetaLodLevels];
    public int BrickCount => meta[VoxelGpuFormat.MetaTotalBricks];
    public int MixedBrickCount => meta[VoxelGpuFormat.MetaMixedBricks];
    public uint Flags => (uint)meta[VoxelGpuFormat.MetaFlags];
    public bool IsEmpty => meta[VoxelGpuFormat.MetaMax] <= meta[VoxelGpuFormat.MetaMin];
    public int3 BoundsMin => new int3(meta[0], meta[1], meta[2]);
    public int3 BoundsMax => new int3(meta[3], meta[4], meta[5]);
    public int CellsPerAxis => meta[VoxelGpuFormat.MetaCellsPerAxis];
    public int CellCount => CellsPerAxis * CellsPerAxis * CellsPerAxis;

    public bool CellEmpty(int cell) => cells[cell * VoxelGpuFormat.CellInts + 3] <= cells[cell * VoxelGpuFormat.CellInts];
    public int3 CellMin(int cell) { int b = cell * VoxelGpuFormat.CellInts; return new int3(cells[b], cells[b + 1], cells[b + 2]); }
    public int3 CellMax(int cell) { int b = cell * VoxelGpuFormat.CellInts; return new int3(cells[b + 3], cells[b + 4], cells[b + 5]); }
    public uint CellFlags(int cell) => (uint)cells[cell * VoxelGpuFormat.CellInts + 6];
    public int CellOcclusion(int cell) => cells[cell * VoxelGpuFormat.CellInts + 7];

    public static VoxelPackResult Create(int maxBricks, int maxCells)
    {
        return new VoxelPackResult
        {
            bricks = new NativeArray<uint>(maxBricks, Allocator.Persistent),
            pages = new NativeList<uint>(VoxelGpuFormat.PageUints * 16, Allocator.Persistent),
            meta = new NativeArray<int>(VoxelGpuFormat.MetaLength, Allocator.Persistent),
            cells = new NativeArray<int>(maxCells * VoxelGpuFormat.CellInts, Allocator.Persistent),
            ecsSpawns = new NativeList<ECSSpawnPoint>(Allocator.Persistent)
        };
    }

    public void Dispose()
    {
        if (bricks.IsCreated) bricks.Dispose();
        if (pages.IsCreated) pages.Dispose();
        if (meta.IsCreated) meta.Dispose();
        if (cells.IsCreated) cells.Dispose();
        if (ecsSpawns.IsCreated) ecsSpawns.Dispose();
    }
}

/// <summary>
/// Converts a chunk's block array into the brick/page format the raymarching shader reads,
/// at every detail level. Replaces GreedyMeshJob: no vertices are produced, the GPU reads
/// block ids directly.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
public struct VoxelPackJob : IJob
{
    [ReadOnly] public NativeArray<byte> blocks;   // (chunkSize + 1)^3, x + y*S + z*S*S
    [ReadOnly] public NativeArray<BlockDatabase.BlockInfoUnmanaged> blockDb;
    public int chunkSize;
    public int3 coord;
    public bool collectECSSpawns;

    public NativeArray<uint> bricks;
    public NativeList<uint> pages;
    public NativeArray<int> meta;
    public NativeArray<int> cells;
    public NativeList<ECSSpawnPoint> ecsSpawns;

    public void Execute()
    {
        int res = VoxelGpuFormat.Resolution(chunkSize);
        int levels = VoxelGpuFormat.LodLevels(res);

        pages.Clear();
        ecsSpawns.Clear();

        // ---- Level 0: full resolution, also gathers bounds / flags / draw cells
        var grid = new NativeArray<byte>(res * res * res, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
        for (int z = 0; z < res; z++)
        for (int y = 0; y < res; y++)
        for (int x = 0; x < res; x++)
            grid[Index(new int3(x, y, z), res)] = GetBlock(x, y, z);

        int cpa = VoxelGpuFormat.CellsPerAxis(res);
        int cellSize = res / cpa;
        for (int c = 0; c < cpa * cpa * cpa; c++)
        {
            int b = c * VoxelGpuFormat.CellInts;
            cells[b + 0] = cells[b + 1] = cells[b + 2] = res;   // min
            cells[b + 3] = cells[b + 4] = cells[b + 5] = -1;    // max (inclusive while scanning)
            cells[b + 6] = 0;
            cells[b + 7] = VoxelGpuFormat.OccFull | VoxelGpuFormat.OccFaceMask;   // cleared per level
        }

        int3 bmin = new int3(res);
        int3 bmax = new int3(-1);
        uint flags = 0;
        int mixed = 0;
        int brickOffset = 0;

        PlaceGrass(grid, res, cellSize, cpa);
        PackLevel(grid, res, brickOffset, ref mixed, true, cellSize, cpa, ref bmin, ref bmax, ref flags);
        Occlusion(grid, res, cpa, true);
        brickOffset += VoxelGpuFormat.BricksInLevel(res, 0);

        bool empty = bmax.x < bmin.x;

        // ---- Coarser levels: each voxel keeps the top-most visible block of its 2x2x2 parent cell
        int levelRes = res;
        for (int level = 1; level < levels && !empty; level++)
        {
            int coarseRes = levelRes >> 1;
            var coarse = new NativeArray<byte>(coarseRes * coarseRes * coarseRes, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            Downsample(grid, levelRes, coarse, coarseRes);
            grid.Dispose();
            grid = coarse;
            levelRes = coarseRes;

            int3 unusedMin = default, unusedMax = default; uint unusedFlags = 0;
            PackLevel(grid, levelRes, brickOffset, ref mixed, false, 0, 0, ref unusedMin, ref unusedMax, ref unusedFlags);
            Occlusion(grid, levelRes, cpa, false);
            brickOffset += VoxelGpuFormat.BricksInLevel(res, level);
        }
        grid.Dispose();

        meta[VoxelGpuFormat.MetaMin + 0] = empty ? 0 : bmin.x;
        meta[VoxelGpuFormat.MetaMin + 1] = empty ? 0 : bmin.y;
        meta[VoxelGpuFormat.MetaMin + 2] = empty ? 0 : bmin.z;
        meta[VoxelGpuFormat.MetaMax + 0] = empty ? 0 : bmax.x + 1;
        meta[VoxelGpuFormat.MetaMax + 1] = empty ? 0 : bmax.y + 1;
        meta[VoxelGpuFormat.MetaMax + 2] = empty ? 0 : bmax.z + 1;
        meta[VoxelGpuFormat.MetaFlags] = (int)flags;
        meta[VoxelGpuFormat.MetaMixedBricks] = mixed;
        meta[VoxelGpuFormat.MetaResolution] = res;
        meta[VoxelGpuFormat.MetaCellsPerAxis] = cpa;
        meta[VoxelGpuFormat.MetaLodLevels] = levels;
        meta[VoxelGpuFormat.MetaTotalBricks] = empty ? VoxelGpuFormat.BricksInLevel(res, 0) : brickOffset;

        // Cell max -> exclusive; empty cells end up with max <= min
        for (int c = 0; c < cpa * cpa * cpa; c++)
        {
            int b = c * VoxelGpuFormat.CellInts;
            if (cells[b + 3] < cells[b + 0])
            {
                cells[b + 0] = cells[b + 1] = cells[b + 2] = 0;
                cells[b + 3] = cells[b + 4] = cells[b + 5] = 0;
                continue;
            }
            cells[b + 3] += 1;
            cells[b + 4] += 1;
            cells[b + 5] += 1;
        }
    }

    void PackLevel(NativeArray<byte> grid, int res, int brickOffset, ref int mixed,
                   bool collectStats, int cellSize, int cpa, ref int3 bmin, ref int3 bmax, ref uint flags)
    {
        int bpa = res / VoxelGpuFormat.BrickSize;

        for (int bz = 0; bz < bpa; bz++)
        for (int by = 0; by < bpa; by++)
        for (int bx = 0; bx < bpa; bx++)
        {
            int3 origin = new int3(bx, by, bz) * VoxelGpuFormat.BrickSize;
            byte first = grid[Index(origin, res)];
            bool uniform = true;
            int solid = 0;

            for (int z = 0; z < VoxelGpuFormat.BrickSize; z++)
            for (int y = 0; y < VoxelGpuFormat.BrickSize; y++)
            for (int x = 0; x < VoxelGpuFormat.BrickSize; x++)
            {
                int3 p = origin + new int3(x, y, z);
                byte id = grid[Index(p, res)];
                if (id != first) uniform = false;

                var vis = Visibility(id);
                if (vis == BlockVisibility.Invisible)
                    continue;
                if (vis != BlockVisibility.Translucent)
                    solid++;

                if (!collectStats)
                    continue;

                bmin = math.min(bmin, p);
                bmax = math.max(bmax, p);
                uint voxelFlag = vis == BlockVisibility.Translucent ? VoxelGpuFormat.FlagHasTranslucent : VoxelGpuFormat.FlagHasOpaque;
                flags |= voxelFlag;

                int3 cc = p / cellSize;
                int cb = (cc.x + (cc.y + cc.z * cpa) * cpa) * VoxelGpuFormat.CellInts;
                cells[cb + 0] = math.min(cells[cb + 0], p.x);
                cells[cb + 1] = math.min(cells[cb + 1], p.y);
                cells[cb + 2] = math.min(cells[cb + 2], p.z);
                cells[cb + 3] = math.max(cells[cb + 3], p.x);
                cells[cb + 4] = math.max(cells[cb + 4], p.y);
                cells[cb + 5] = math.max(cells[cb + 5], p.z);
                cells[cb + 6] |= (int)voxelFlag;
            }

            int brickIndex = brickOffset + bx + (by + bz * bpa) * bpa;
            if (uniform)
            {
                bricks[brickIndex] = VoxelGpuFormat.UniformFlag | first;
                continue;
            }

            bricks[brickIndex] = (uint)mixed++ | (solid * 2 >= VoxelGpuFormat.BrickVoxels ? VoxelGpuFormat.DenseFlag : 0u);
            for (int li = 0; li < VoxelGpuFormat.BrickVoxels; li += 4)
            {
                uint word = 0;
                for (int k = 0; k < 4; k++)
                {
                    int l = li + k;
                    int3 p = origin + new int3(l & 7, (l >> 3) & 7, l >> 6);
                    word |= (uint)grid[Index(p, res)] << (k * 8);
                }
                pages.Add(word);
            }
        }
    }

    // Clears cell occlusion bits that don't hold at this detail level. Coarse levels are checked
    // too: downsampling can put water or leaves into a boundary layer that is opaque at full
    // resolution, and the neighbour cell would then be visible through it at a distance.
    void Occlusion(NativeArray<byte> grid, int res, int cpa, bool checkFull)
    {
        int cs = res / cpa;
        int last = cs - 1;
        for (int cz = 0; cz < cpa; cz++)
        for (int cy = 0; cy < cpa; cy++)
        for (int cx = 0; cx < cpa; cx++)
        {
            int b = (cx + (cy + cz * cpa) * cpa) * VoxelGpuFormat.CellInts;
            int occ = cells[b + 7];
            if (occ == 0)
                continue;
            int3 o = new int3(cx, cy, cz) * cs;

            if (checkFull && (occ & VoxelGpuFormat.OccFull) != 0)
            {
                for (int z = 0; z < cs && (occ & VoxelGpuFormat.OccFull) != 0; z++)
                for (int y = 0; y < cs; y++)
                for (int x = 0; x < cs; x++)
                {
                    if (!IsOpaque(grid, o + new int3(x, y, z), res))
                    {
                        occ &= ~VoxelGpuFormat.OccFull;
                        y = cs;
                        break;
                    }
                }
            }

            // A fully opaque cell has opaque faces at every level (downsampling keeps opaque blocks).
            if ((occ & VoxelGpuFormat.OccFull) == 0)
            {
                for (int f = 0; f < 6; f++)
                {
                    if ((occ & (1 << f)) == 0)
                        continue;
                    int axis = f >> 1;
                    int layer = (f & 1) == 0 ? 0 : last;
                    int ua = (axis + 1) % 3, va = (axis + 2) % 3;
                    bool opaque = true;
                    for (int v = 0; v < cs && opaque; v++)
                    for (int u = 0; u < cs; u++)
                    {
                        int3 p = o;
                        p[axis] += layer;
                        p[ua] += u;
                        p[va] += v;
                        if (!IsOpaque(grid, p, res)) { opaque = false; break; }
                    }
                    if (!opaque)
                        occ &= ~(1 << f);
                }
            }
            cells[b + 7] = occ;
        }
    }

    bool IsOpaque(NativeArray<byte> grid, int3 p, int res) => Visibility(grid[Index(p, res)]) == BlockVisibility.Opaque;

    BlockVisibility Visibility(byte id) => id == VoxelGpuFormat.GrassMarker ? BlockVisibility.Invisible : blockDb[id].visibility;

    // Marks the air above every exposed grass top (level 0 only) and grows the draw cell of the
    // grass block by one voxel upwards so its box covers the blades. Grass in the top layer has its
    // blades in the chunk above: the cell box reaches one voxel past the chunk and the shader
    // decides there from the block below (VoxelFetchMarch).
    void PlaceGrass(NativeArray<byte> grid, int res, int cellSize, int cpa)
    {
        for (int z = 0; z < res; z++)
        for (int y = 0; y < res; y++)
        for (int x = 0; x < res; x++)
        {
            byte id = grid[Index(new int3(x, y, z), res)];
            if (id == VoxelGpuFormat.GrassMarker || blockDb[id].surfaceDecoration != SurfaceDecoration.Grass)
                continue;

            bool top = y + 1 >= res;
            byte above = top ? GetBlock(x, math.min(res, chunkSize), z) : grid[Index(new int3(x, y + 1, z), res)];
            if (above == VoxelGpuFormat.GrassMarker || blockDb[above].visibility != BlockVisibility.Invisible)
                continue;
            if (!top)
                grid[Index(new int3(x, y + 1, z), res)] = VoxelGpuFormat.GrassMarker;

            int3 cc = new int3(x, y, z) / cellSize;
            int cb = (cc.x + (cc.y + cc.z * cpa) * cpa) * VoxelGpuFormat.CellInts;
            cells[cb + 4] = math.max(cells[cb + 4], y + 1);   // inclusive max, the voxel's own bounds come from PackLevel
        }
    }

    // Keeps the top-most visible block of each 2x2x2 cell so surfaces keep their material
    // (grass stays on top of dirt when seen from a distance).
    void Downsample(NativeArray<byte> fine, int fineRes, NativeArray<byte> coarse, int coarseRes)
    {
        for (int z = 0; z < coarseRes; z++)
        for (int y = 0; y < coarseRes; y++)
        for (int x = 0; x < coarseRes; x++)
        {
            byte best = 0;
            for (int dy = 1; dy >= 0 && best == 0; dy--)
            for (int dz = 0; dz < 2 && best == 0; dz++)
            for (int dx = 0; dx < 2; dx++)
            {
                byte id = fine[Index(new int3(x * 2 + dx, y * 2 + dy, z * 2 + dz), fineRes)];
                if (Visibility(id) != BlockVisibility.Invisible)
                {
                    best = id;
                    break;
                }
            }
            coarse[Index(new int3(x, y, z), coarseRes)] = best;
        }
    }

    static int Index(int3 p, int res) => p.x + (p.y + p.z * res) * res;

    byte GetBlock(int x, int y, int z)
    {
        int s = chunkSize + 1;
        return blocks[x + y * s + z * s * s];
    }
}

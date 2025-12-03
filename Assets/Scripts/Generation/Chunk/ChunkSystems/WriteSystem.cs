using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

public class WriteSystem
{
    private readonly Dictionary<int3, List<PendingBlockWrite>> pendingWrites =
        new Dictionary<int3, List<PendingBlockWrite>>();
    public int PendingWriteChunks => pendingWrites.Count;

    // External state
    private Func<int3, bool> isDecorating;
    private Func<int3, bool> isMeshing;
    private Func<int3, bool> hasBlockIds;
    private Func<int3, NativeArray<byte>> getBlockIds;
    private Action<int3> markForRemesh;

    private int chunkSize;

    private Func<int3, float> getDistance;             // distance to player (ChunkManager supplies this)
    private float forceGenRange;                       // usually midRange
    private Action<int3> queueFrontier;                // request chunk generation
    private HashSet<(int3, int3, byte)> mirrorSeen = new();   // suppress duplicate mirror writes

    public void Initialize(
        int chunkSize,
        Func<int3, bool> isDecorating,
        Func<int3, bool> isMeshing,
        Func<int3, bool> hasBlockIds,
        Func<int3, NativeArray<byte>> getBlockIds,
        Action<int3> markForRemesh,

        // NEW optional inputs – you will patch ChunkManager later
        Func<int3, float> getDistance = null,
        float forceGenRange = 9999f,
        Action<int3> queueFrontier = null
    )
    {
        this.chunkSize = chunkSize;
        this.isDecorating = isDecorating;
        this.isMeshing = isMeshing;
        this.hasBlockIds = hasBlockIds;
        this.getBlockIds = getBlockIds;
        this.markForRemesh = markForRemesh;

        this.getDistance = getDistance;
        this.forceGenRange = forceGenRange;
        this.queueFrontier = queueFrontier;
    }

    public void EnqueueWrite(PendingBlockWrite w)
    {
        if (!pendingWrites.TryGetValue(w.targetChunk, out var list))
            pendingWrites[w.targetChunk] = list = new List<PendingBlockWrite>();
        list.Add(w);
    }

    // ================================================================
    // UPDATED: Now handles:
    // - decorating + meshing lockout
    // - spill into ungenerated chunks
    // - force-generate under terrain
    // ================================================================
    public void ProcessWrite(int3 coord, PendingBlockWrite w)
    {
        // If chunk is occupied or missing blockIds ? delay
        if (isDecorating(coord) || isMeshing(coord) || !hasBlockIds(coord))
        {
            // NEW: If chunk has no data AND no generation in progress ? force-generate
            if (!hasBlockIds(coord) && getDistance != null && queueFrontier != null)
            {
                if (getDistance(coord) < forceGenRange)
                    queueFrontier(coord);
            }

            EnqueueWrite(w);
            return;
        }

        // Chunk is ready - apply write
        var blockIds = getBlockIds(coord);
        ApplyWriteDirect(coord, blockIds, w);

        // Centralized remesh trigger
        markForRemesh(coord);
    }

    private void ApplyWriteDirect(int3 coord, NativeArray<byte> blockIds, PendingBlockWrite w)
    {
        int s = chunkSize + 1;
        int3 p = w.localPos;

        if ((uint)p.x >= s || (uint)p.y >= s || (uint)p.z >= s)
            return;

        int index = p.x + p.y * s + p.z * s * s;
        byte current = blockIds[index];

        switch (w.mode)
        {
            case PendingWriteMode.Replace:
                blockIds[index] = w.blockId;
                break;

            case PendingWriteMode.ReplaceAir:
            case PendingWriteMode.ReplaceSoft:
                if (current == 0)
                    blockIds[index] = w.blockId;
                break;
        }

        // prevent doubled mirror propagation
        if (!w.isMirror)
            EnqueueBoundaryMirrors(coord, w);
    }

    // BOUNDARY MIRRORING (patched to suppress duplicates)
    private void EnqueueBoundaryMirrors(int3 coord, PendingBlockWrite source)
    {
        int3 p = source.localPos;
        int edge = chunkSize;

        void Mirror(int3 neighborChunk, int3 pos)
        {
            var key = (neighborChunk, pos, source.blockId);

            // duplicate suppression
            if (!mirrorSeen.Add(key))
                return;

            var m = new PendingBlockWrite
            {
                targetChunk = neighborChunk,
                localPos = pos,
                blockId = source.blockId,
                mode = source.mode,
                isMirror = true
            };

            EnqueueWrite(m);
        }

        if (p.x == 0)
            Mirror(coord + new int3(-1, 0, 0), new int3(edge, p.y, p.z));

        if (p.y == 0)
            Mirror(coord + new int3(0, -1, 0), new int3(p.x, edge, p.z));

        if (p.z == 0)
            Mirror(coord + new int3(0, 0, -1), new int3(p.x, p.y, edge));
    }

    // SAFELY USED BY CHUNKMANAGER
    public List<PendingBlockWrite> TakeWrites(int3 coord)
    {
        if (!pendingWrites.TryGetValue(coord, out var list))
            return new List<PendingBlockWrite>(0);

        pendingWrites.Remove(coord);
        return list;
    }

    public bool HasWritesFor(int3 coord) => pendingWrites.ContainsKey(coord);

    public List<int3> GetKeySnapshot() => new List<int3>(pendingWrites.Keys);

    public void Clear(int3 coord) => pendingWrites.Remove(coord);

    public void ClearAll()
    {
        pendingWrites.Clear();
        mirrorSeen.Clear();
    }
}

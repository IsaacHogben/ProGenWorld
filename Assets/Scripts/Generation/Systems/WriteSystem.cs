using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

public class WriteSystem
{
    // one list per chunk coord
    private readonly Dictionary<int3, List<PendingBlockWrite>> pendingWrites =
        new Dictionary<int3, List<PendingBlockWrite>>();

    // pooled lists to avoid allocations
    private readonly Stack<List<PendingBlockWrite>> listPool =
        new Stack<List<PendingBlockWrite>>();

    public int PendingWritesCount => pendingWrites.Count;

    // External state
    private Func<int3, bool> isDecorating;
    private Func<int3, bool> isMeshing;
    private Func<int3, bool> hasBlockIds;
    private Func<int3, NativeArray<byte>> getBlockIds;
    private Action<int3> markForRemesh;

    private int chunkSize;

    private Func<int3, float> getDistance;   // distance to player
    private float forceGenRange;             // usually midRange
    private Action<int3> queueFrontier;      // request chunk generation

    private HashSet<(int3, int3, byte)> mirrorSeen = new(); // suppress duplicate mirror writes

    public void Initialize(
        int chunkSize,
        Func<int3, bool> isDecorating,
        Func<int3, bool> isMeshing,
        Func<int3, bool> hasBlockIds,
        Func<int3, NativeArray<byte>> getBlockIds,
        Action<int3> markForRemesh,
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

    // -----------------------------
    // Enqueue
    // -----------------------------
    public void EnqueueWrite(PendingBlockWrite w)
    {
        if (!pendingWrites.TryGetValue(w.targetChunk, out var list))
        {
            list = (listPool.Count > 0)
                ? listPool.Pop()
                : new List<PendingBlockWrite>(16);

            list.Clear();
            pendingWrites[w.targetChunk] = list;
        }

        list.Add(w);
    }

    // -----------------------------
    // Process single write
    // -----------------------------
    public void ProcessWrite(int3 coord, PendingBlockWrite w)
    {
        // If chunk is occupied or missing blockIds - delay
        if (isDecorating(coord) || isMeshing(coord) || !hasBlockIds(coord))
        {
            if (!hasBlockIds(coord) && getDistance != null && queueFrontier != null)
            {
                if (getDistance(coord) < forceGenRange)
                    queueFrontier(coord);
            }

            EnqueueWrite(w);
            return;
        }

        var blockIds = getBlockIds(coord);
        ApplyWriteDirect(coord, blockIds, w);
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

        if (!w.isMirror)
            EnqueueBoundaryMirrors(coord, w);
    }

    private void EnqueueBoundaryMirrors(int3 coord, PendingBlockWrite source)
    {
        int3 p = source.localPos;
        int edge = chunkSize;

        void Mirror(int3 neighborChunk, int3 pos)
        {
            var key = (neighborChunk, pos, source.blockId);
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



    // -----------------------------
    // Access from ChunkManager
    // -----------------------------
    // Called by ChunkManager.FlushAllPendingWrites
    public List<PendingBlockWrite> StealWrites(int3 coord)
    {
        if (!pendingWrites.TryGetValue(coord, out var list))
            return null;

        // Detach from dictionary so any new writes go into a NEW list
        pendingWrites.Remove(coord);
        return list;
    }

    public void RecycleList(List<PendingBlockWrite> list)
    {
        if (list == null)
            return;

        list.Clear();
        listPool.Push(list);
    }

    // Fills caller-provided list with keys – NO allocation
    public void GetKeySnapshot(List<int3> dst)
    {
        dst.Clear();
        foreach (var kvp in pendingWrites)
            dst.Add(kvp.Key);
    }

    public void Clear(int3 coord)
    {
        if (!pendingWrites.TryGetValue(coord, out var list))
            return;

        pendingWrites.Remove(coord);
        list.Clear();
        listPool.Push(list);
    }

    public void ClearAll()
    {
        foreach (var kvp in pendingWrites)
        {
            var list = kvp.Value;
            list.Clear();
            listPool.Push(list);
        }

        pendingWrites.Clear();
        mirrorSeen.Clear();
    }
}

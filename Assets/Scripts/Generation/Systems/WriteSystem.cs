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
    public bool HasPendingWrites(int3 coord) => pendingWrites.ContainsKey(coord);

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

    // Suppresses duplicate mirror writes, keyed by the chunk receiving the mirror so an unloaded
    // chunk's entries can be dropped and it receives mirrors again when it regenerates.
    private readonly Dictionary<int3, HashSet<(int3, byte)>> mirrorSeen = new();

    // Cross-chunk decoration writes, kept after they are applied so a chunk that unloads and
    // regenerates gets its neighbours' blocks back. Keyed by the chunk that emitted them and
    // dropped when that chunk unloads, since it emits them again when it is next decorated.
    private readonly Dictionary<int3, Dictionary<int3, List<PendingBlockWrite>>> storedBySource = new();
    private readonly Dictionary<int3, HashSet<int3>> sourcesByTarget = new();
    private readonly Stack<Dictionary<int3, List<PendingBlockWrite>>> targetMapPool = new();
    private readonly Stack<HashSet<int3>> sourceSetPool = new();

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
    // Decoration output arrives in spatially coherent runs, so most writes hit the same list as the last one.
    private int3 lastEnqueueCoord;
    private List<PendingBlockWrite> lastEnqueueList;

    public void EnqueueWrite(PendingBlockWrite w)
    {
        if (lastEnqueueList != null && lastEnqueueCoord.Equals(w.targetChunk))
        {
            lastEnqueueList.Add(w);
            return;
        }

        if (!pendingWrites.TryGetValue(w.targetChunk, out var list))
        {
            list = RentList();
            pendingWrites[w.targetChunk] = list;
        }

        list.Add(w);
        lastEnqueueCoord = w.targetChunk;
        lastEnqueueList = list;
    }

    // -----------------------------
    // Decoration output
    // -----------------------------
    // Enqueues a chunk's decoration writes and stores the ones that land in other chunks.
    public void EnqueueDecorationWrites(int3 source, NativeList<PendingBlockWrite> writes)
    {
        DropStoredWrites(source); // a re-decorated chunk replaces what it sent before

        Dictionary<int3, List<PendingBlockWrite>> byTarget = null;
        int3 lastTarget = default;
        List<PendingBlockWrite> lastList = null;

        for (int i = 0; i < writes.Length; i++)
        {
            var w = writes[i];
            EnqueueWrite(w);

            // Writes into the source chunk itself are regenerated along with it
            if (w.targetChunk.Equals(source))
                continue;

            if (lastList == null || !lastTarget.Equals(w.targetChunk))
            {
                byTarget ??= targetMapPool.Count > 0 ? targetMapPool.Pop() : new Dictionary<int3, List<PendingBlockWrite>>();
                if (!byTarget.TryGetValue(w.targetChunk, out lastList))
                {
                    lastList = RentList();
                    byTarget[w.targetChunk] = lastList;

                    if (!sourcesByTarget.TryGetValue(w.targetChunk, out var sources))
                    {
                        sources = sourceSetPool.Count > 0 ? sourceSetPool.Pop() : new HashSet<int3>();
                        sourcesByTarget[w.targetChunk] = sources;
                    }
                    sources.Add(source);
                }
                lastTarget = w.targetChunk;
            }
            lastList.Add(w);
        }

        if (byTarget != null)
            storedBySource[source] = byTarget;
    }

    // Writes a chunk sends after its decoration (e.g. a stream arriving from a neighbour and running
    // on): enqueued and added to what the chunk already sent, so they replay like decoration writes.
    public void EnqueueExtraWrites(int3 source, NativeList<PendingBlockWrite> writes)
    {
        storedBySource.TryGetValue(source, out var byTarget);
        for (int i = 0; i < writes.Length; i++)
        {
            var w = writes[i];
            EnqueueWrite(w);
            if (w.targetChunk.Equals(source))
                continue;

            if (byTarget == null)
            {
                byTarget = targetMapPool.Count > 0 ? targetMapPool.Pop() : new Dictionary<int3, List<PendingBlockWrite>>();
                storedBySource[source] = byTarget;
            }
            if (!byTarget.TryGetValue(w.targetChunk, out var list))
            {
                list = RentList();
                byTarget[w.targetChunk] = list;
                if (!sourcesByTarget.TryGetValue(w.targetChunk, out var sources))
                {
                    sources = sourceSetPool.Count > 0 ? sourceSetPool.Pop() : new HashSet<int3>();
                    sourcesByTarget[w.targetChunk] = sources;
                }
                sources.Add(source);
            }
            list.Add(w);
        }
    }

    // Called when a chunk gets fresh block data: re-enqueues every stored write its loaded
    // neighbours sent it. Queued writes from those neighbours are replaced rather than doubled.
    public void ReplayStoredWrites(int3 target)
    {
        if (!sourcesByTarget.TryGetValue(target, out var sources))
            return;

        if (pendingWrites.TryGetValue(target, out var queued))
        {
            // Keep mirrors (not stored); everything else is about to be re-enqueued from the store
            int kept = 0;
            for (int i = 0; i < queued.Count; i++)
                if (queued[i].isMirror)
                    queued[kept++] = queued[i];
            queued.RemoveRange(kept, queued.Count - kept);
        }

        foreach (var source in sources)
        {
            var stored = storedBySource[source][target];
            for (int i = 0; i < stored.Count; i++)
                EnqueueWrite(stored[i]);
        }
    }

    // Chunks that stored writes are waiting on but that have no block data yet.
    public void GetTargetsAwaitingBlocks(List<int3> dst)
    {
        dst.Clear();
        foreach (var target in sourcesByTarget.Keys)
            if (!hasBlockIds(target))
                dst.Add(target);
    }

    private void DropStoredWrites(int3 source)
    {
        if (!storedBySource.Remove(source, out var byTarget))
            return;

        foreach (var kvp in byTarget)
        {
            if (sourcesByTarget.TryGetValue(kvp.Key, out var sources))
            {
                sources.Remove(source);
                if (sources.Count == 0)
                {
                    sourcesByTarget.Remove(kvp.Key);
                    sourceSetPool.Push(sources);
                }
            }
            RecycleList(kvp.Value);
        }

        byTarget.Clear();
        targetMapPool.Push(byTarget);
    }

    // -----------------------------
    // Process a chunk's batch
    // -----------------------------
    // Returns false when the chunk is busy; the write system then keeps the batch,
    // so the caller must not recycle it.
    public bool ProcessBatch(int3 coord, List<PendingBlockWrite> batch)
    {
        if (!hasBlockIds(coord))
        {
            if (getDistance != null && queueFrontier != null && getDistance(coord) < forceGenRange)
                queueFrontier(coord);

            // Drop the batch: the store replays these when the chunk generates, and a sender that
            // has since unloaded sends them again when it is next decorated. Holding them would let
            // writes for chunks that never load pile up and be re-checked every frame.
            return true;
        }

        if (isDecorating(coord) || isMeshing(coord))
        {
            // Keep the whole batch, ahead of anything enqueued for this coord since it was stolen.
            if (pendingWrites.TryGetValue(coord, out var newer))
            {
                batch.AddRange(newer);
                newer.Clear();
                listPool.Push(newer);
            }
            pendingWrites[coord] = batch;
            InvalidateEnqueueCache();
            return false;
        }

        var blockIds = getBlockIds(coord);
        for (int i = 0; i < batch.Count; i++)
            ApplyWriteDirect(coord, blockIds, batch[i]);
        markForRemesh(coord);
        return true;
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

        if (w.mode == PendingWriteMode.DropToSurface)
        {
            if (!BlockWriter.TryFindSurface(blockIds, s, chunkSize, ref p))
                return;
            w.localPos = p;
            w.mode = PendingWriteMode.ReplaceAir;
        }

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

            case PendingWriteMode.ReplaceSolid:
                if (current != (byte)BlockType.Air && current != (byte)BlockType.Water)
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
            if (!mirrorSeen.TryGetValue(neighborChunk, out var seen))
            {
                seen = new HashSet<(int3, byte)>();
                mirrorSeen[neighborChunk] = seen;
            }
            if (!seen.Add((pos, source.blockId)))
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
        InvalidateEnqueueCache();
        return list;
    }

    private List<PendingBlockWrite> RentList()
        => listPool.Count > 0 ? listPool.Pop() : new List<PendingBlockWrite>(16);

    public void RecycleList(List<PendingBlockWrite> list)
    {
        if (list == null)
            return;

        list.Clear();
        listPool.Push(list);
    }

    // Fills caller-provided list with keys � NO allocation
    public void GetKeySnapshot(List<int3> dst)
    {
        dst.Clear();
        foreach (var kvp in pendingWrites)
            dst.Add(kvp.Key);
    }

    // Called when a chunk unloads. Writes waiting for it are dropped (its neighbours' are replayed
    // from the store when it regenerates), as are the writes it sent (it sends them again).
    public void OnChunkUnloaded(int3 coord)
    {
        if (pendingWrites.Remove(coord, out var list))
        {
            InvalidateEnqueueCache();
            RecycleList(list);
        }

        DropStoredWrites(coord);
        mirrorSeen.Remove(coord);
    }

    public void ClearAll()
    {
        foreach (var kvp in pendingWrites)
            RecycleList(kvp.Value);
        pendingWrites.Clear();

        tmpSources.Clear();
        tmpSources.AddRange(storedBySource.Keys);
        foreach (var source in tmpSources)
            DropStoredWrites(source);

        mirrorSeen.Clear();
        InvalidateEnqueueCache();
    }

    private readonly List<int3> tmpSources = new();

    private void InvalidateEnqueueCache() => lastEnqueueList = null;
}

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Hands streams from chunk to chunk. Every stream arriving at a chunk is kept (keyed by stream
/// and length, so a resent one is not doubled): it runs with the chunk's decoration whenever the
/// chunk is (re)decorated, and if the chunk is already decorated when it arrives, in a short
/// stream-only job as soon as the chunk's blocks are free.
/// </summary>
public sealed class StreamSystem : IDisposable
{
    public struct Config
    {
        public int chunkSize;
        public int seed;
        public int waterLevel;
        public BiomeDataManager biomeDataManager;

        public Func<int3, bool> canRunNow;                    // decorated, blocks loaded and not in use
        public Func<int3, NativeArray<byte>> getBlocks;       // full resolution blocks
        public Action<int3> onStarted;                        // the chunk's blocks are being written
        public Action<int3, NativeList<PendingBlockWrite>> onFinished;   // writes to send on (caller keeps nothing)
        public Func<bool> logEnds;                            // log where and why each stream ends
    }

    readonly Config cfg;
    NativeArray<StreamBiomeData> biomes;
    NativeArray<BiomeHint> noHints;

    readonly Dictionary<int3, Dictionary<long, StreamHead>> arrived = new();

    // Columns holding stream or pond water, kept per chunk (while it has any) so later
    // stream-only runs and fill seeds know a stream's water from a lake's
    readonly Dictionary<int3, NativeArray<byte>> visitedMaps = new();
    readonly HashSet<int3> decorating = new();
    readonly HashSet<int3> unloadedWhileDecorating = new();
    readonly Dictionary<int3, List<StreamHead>> unapplied = new();   // arrived after the chunk was decorated

    struct Running
    {
        public JobHandle handle;
        public NativeArray<StreamHead> heads;
        public NativeList<StreamHead> outgoing;
        public NativeList<PendingBlockWrite> writes;
        public NativeList<StreamEnd> ends;
    }

    readonly Dictionary<int3, Running> running = new();
    readonly List<int3> tmpKeys = new();

    public NativeArray<StreamBiomeData> Biomes => biomes;
    public int ActiveJobs => running.Count;

    public StreamSystem(Config config)
    {
        cfg = config;
        var defs = config.biomeDataManager.GetBiomeDefinitions();
        biomes = new NativeArray<StreamBiomeData>(defs.Length, Allocator.Persistent);
        foreach (var biome in config.biomeDataManager.biomes)
            if (biome != null)
                biomes[biome.BiomeID] = StreamBiomeData.From(biome.streams, (byte)biome.surfaceBlock, (byte)biome.sedimentBlock, (byte)biome.subsurfaceBlock);
        noHints = new NativeArray<BiomeHint>(1, Allocator.Persistent);
    }

    /// <summary>
    /// Every stream that has arrived at a chunk, for its decoration run (which applies them all,
    /// so none is left waiting for a stream-only run).
    /// </summary>
    public NativeArray<StreamHead> TakeForDecoration(int3 coord, Allocator allocator)
    {
        unapplied.Remove(coord);
        if (!arrived.TryGetValue(coord, out var heads))
            return new NativeArray<StreamHead>(0, allocator);
        var array = new NativeArray<StreamHead>(heads.Count, allocator);
        int i = 0;
        foreach (var h in heads.Values)
            array[i++] = h;
        return array;
    }

    /// <summary>Streams leaving a chunk: kept for the chunk they enter, and run there if it is already decorated.</summary>
    public void Route(NativeList<StreamHead> heads)
    {
        for (int i = 0; i < heads.Length; i++)
        {
            var h = heads[i];
            if (h.fill == 2)
                LowerWaiting(h);
            if (!arrived.TryGetValue(h.chunk, out var map))
                arrived[h.chunk] = map = new Dictionary<long, StreamHead>();
            if (!map.TryAdd(h.Key, h))
                continue;
            if (!unapplied.TryGetValue(h.chunk, out var list))
                unapplied[h.chunk] = list = new List<StreamHead>();
            list.Add(h);
        }
    }

    // A pond lowered: its seeds kept for the chunk (still waiting, or for when it is generated
    // again) flood at the new level
    readonly List<long> tmpSeedKeys = new();
    void LowerWaiting(in StreamHead lower)
    {
        if (arrived.TryGetValue(lower.chunk, out var map))
        {
            tmpSeedKeys.Clear();
            foreach (var kv in map)
                if (Lowers(kv.Value, lower))
                    tmpSeedKeys.Add(kv.Key);
            foreach (var key in tmpSeedKeys)
            {
                var seed = map[key];
                seed.level = lower.level;
                map[key] = seed;
            }
        }
        if (unapplied.TryGetValue(lower.chunk, out var list))
            for (int i = 0; i < list.Count; i++)
                if (Lowers(list[i], lower))
                {
                    var seed = list[i];
                    seed.level = lower.level;
                    list[i] = seed;
                }
    }

    static bool Lowers(in StreamHead seed, in StreamHead lower)
        => seed.fill == 1 && seed.id == lower.id && seed.level > lower.level && seed.level <= lower.budget;

    public void Update()
    {
        // Finished stream-only runs
        if (running.Count > 0)
        {
            tmpKeys.Clear();
            tmpKeys.AddRange(running.Keys);
            foreach (var coord in tmpKeys)
            {
                var r = running[coord];
                if (!r.handle.IsCompleted)
                    continue;
                r.handle.Complete();
                running.Remove(coord);
                Route(r.outgoing);
                Report(r.ends);
                cfg.onFinished(coord, r.writes);
                r.writes.Dispose();
                r.outgoing.Dispose();
                r.heads.Dispose();
                r.ends.Dispose();
                TrimVisited(coord);
            }
        }

        // Streams waiting on decorated chunks
        if (unapplied.Count == 0)
            return;
        tmpKeys.Clear();
        tmpKeys.AddRange(unapplied.Keys);
        foreach (var coord in tmpKeys)
        {
            if (running.ContainsKey(coord) || !cfg.canRunNow(coord))
                continue;
            var list = unapplied[coord];
            unapplied.Remove(coord);
            Schedule(coord, list);
        }
    }

    void Schedule(int3 coord, List<StreamHead> list)
    {
        var heads = new NativeArray<StreamHead>(list.Count, Allocator.Persistent);
        for (int i = 0; i < list.Count; i++)
            heads[i] = list[i];

        var r = new Running
        {
            heads = heads,
            outgoing = new NativeList<StreamHead>(Allocator.Persistent),
            writes = new NativeList<PendingBlockWrite>(Allocator.Persistent),
            ends = new NativeList<StreamEnd>(Allocator.Persistent)
        };
        cfg.onStarted(coord);
        r.handle = CreateJob(coord, cfg.getBlocks(coord), r.writes, noHints, 0, heads, r.outgoing, Visited(coord), r.ends, false).Schedule();
        running[coord] = r;
    }

    /// <summary>A stream job for a chunk (the decoration system chains one after each decoration job).</summary>
    public StreamJob CreateJob(int3 coord, NativeArray<byte> blockIds, NativeList<PendingBlockWrite> writes,
        NativeArray<BiomeHint> hints, int hintResolution, NativeArray<StreamHead> heads,
        NativeList<StreamHead> outgoing, NativeArray<byte> visited, NativeList<StreamEnd> ends, bool spawnSources)
    {
        return new StreamJob
        {
            chunkCoord = coord,
            chunkSize = cfg.chunkSize,
            indexSize = cfg.chunkSize + 1,
            waterLevel = cfg.waterLevel,
            seed = (uint)cfg.seed,
            spawnSources = (byte)(spawnSources ? 1 : 0),
            blockIds = blockIds,
            pendingWrites = writes,
            biomeHints = hints,
            biomeResolution = hintResolution,
            biomes = biomes,
            incoming = heads,
            outgoing = outgoing,
            visited = visited,
            ends = ends
        };
    }

    NativeArray<byte> Visited(int3 coord)
    {
        if (!visitedMaps.TryGetValue(coord, out var v))
            visitedMaps[coord] = v = new NativeArray<byte>(cfg.chunkSize * cfg.chunkSize, Allocator.Persistent);
        return v;
    }

    // Keeps a chunk's map only while it has stream water
    void TrimVisited(int3 coord)
    {
        if (!visitedMaps.TryGetValue(coord, out var v))
            return;
        for (int i = 0; i < v.Length; i++)
            if (v[i] != 0)
                return;
        v.Dispose();
        visitedMaps.Remove(coord);
    }

    void DropVisited(int3 coord)
    {
        if (visitedMaps.Remove(coord, out var v))
            v.Dispose();
    }

    /// <summary>The chunk's (fresh) blocks are being decorated: its stream map starts empty.</summary>
    public NativeArray<byte> BeginDecoration(int3 coord)
    {
        decorating.Add(coord);
        unloadedWhileDecorating.Remove(coord);
        var v = Visited(coord);
        for (int i = 0; i < v.Length; i++)
            v[i] = 0;
        return v;
    }

    public void EndDecoration(int3 coord)
    {
        decorating.Remove(coord);
        if (unloadedWhileDecorating.Remove(coord))
            DropVisited(coord);
        else
            TrimVisited(coord);
    }

    /// <summary>Logs where and why streams ended, when ChunkManager > Log Stream Ends is on.</summary>
    public void Report(NativeList<StreamEnd> ends)
    {
        if (cfg.logEnds == null || !cfg.logEnds())
            return;
        for (int i = 0; i < ends.Length; i++)
        {
            var e = ends[i];
            UnityEngine.Debug.Log($"[Streams] stream {e.id:X8} ended at {e.position}: {e.reason} after {e.length} blocks");
        }
    }

    /// <summary>A chunk unloads: a stream-only run on it is dropped (its streams run again with its next decoration).</summary>
    public void OnChunkUnloaded(int3 coord)
    {
        unapplied.Remove(coord);
        if (running.Remove(coord, out var r))
        {
            r.handle.Complete();
            r.writes.Dispose();
            r.outgoing.Dispose();
            r.heads.Dispose();
            r.ends.Dispose();
        }
        if (decorating.Contains(coord))
            unloadedWhileDecorating.Add(coord);   // its decoration job still uses the map
        else
            DropVisited(coord);
    }

    public bool IsRunning(int3 coord) => running.ContainsKey(coord);

    public void Dispose()
    {
        foreach (var r in running.Values)
        {
            r.handle.Complete();
            r.writes.Dispose();
            r.outgoing.Dispose();
            r.heads.Dispose();
            r.ends.Dispose();
        }
        running.Clear();
        foreach (var v in visitedMaps.Values)
            v.Dispose();
        visitedMaps.Clear();
        if (biomes.IsCreated) biomes.Dispose();
        if (noHints.IsCreated) noHints.Dispose();
    }
}

using System;
using System.Collections.Generic;
using Unity.Mathematics;

/// <summary>
/// Region generator scheduling: walks chunk columns nearest the player first, asks RegionSystem for
/// each column's height band (how far its terrain, water and 3D shapes can reach) and queues the
/// chunks around its surface. From those the flood fill grows through faces where terrain crosses
/// (ChunkManager), but only into chunks inside their column's band: open sky and buried rock are
/// never generated, and 3D shapes only cost chunks where they actually reach.
/// </summary>
public sealed class ColumnScheduler
{
    readonly int chunkSize;
    readonly RegionSystem regions;

    readonly Dictionary<int2, int4> bands = new();   // column -> chunk y range (x, y) and its surface chunks (z, w), inclusive
    readonly HashSet<int2> expanded = new();         // columns whose band chunks were queued
    int2[] offsets = Array.Empty<int2>();            // column offsets within range, nearest first
    int offsetRadius = -1;

    int minChunkY = int.MaxValue, maxChunkY = int.MinValue;
    int ticksSinceCheck;

    // Ticks between re-checks of queued columns: chunks can be dropped later (unloaded or left
    // behind by 3D distance, e.g. while flying high) and must be queued again when back in range
    const int RecheckEvery = 30;

    /// <summary>Lowest / highest chunk y of any known column band (int.MaxValue / MinValue before any).</summary>
    public int MinChunkY => minChunkY;
    public int MaxChunkY => maxChunkY;

    /// <summary>True while columns in range were left unqueued by the last Tick (bands pending or throttled).</summary>
    public bool Busy { get; private set; } = true;

    public ColumnScheduler(RegionSystem regions, int chunkSize)
    {
        this.regions = regions;
        this.chunkSize = chunkSize;
        regions.OnColumnBand += HandleBand;
    }

    // Margin around the surface for the chunks queued first (the surface between samples, shallow
    // 3D shapes): the flood fill finds the rest
    const float SurfaceMargin = 4f;

    void HandleBand(int2 column, float minY, float maxY, float surfaceMin, float surfaceMax)
    {
        // Chunks share their top face with the chunk above (chunkSize + 1 samples), so a surface
        // exactly on a boundary is in the lower chunk
        int lo = (int)math.floor((minY - 1f) / chunkSize);
        int hi = (int)math.floor(maxY / chunkSize);
        int surfaceLo = math.clamp((int)math.floor((surfaceMin - SurfaceMargin - 1f) / chunkSize), lo, hi);
        int surfaceHi = math.clamp((int)math.floor((surfaceMax + SurfaceMargin) / chunkSize), surfaceLo, hi);
        bands[column] = new int4(lo, hi, surfaceLo, surfaceHi);
        minChunkY = math.min(minChunkY, lo);
        maxChunkY = math.max(maxChunkY, hi);
    }

    /// <summary>False while the column's band is unknown: its chunks may still hold terrain.</summary>
    public bool IsColumnKnown(int2 column) => bands.ContainsKey(column);

    public bool TryGetBand(int2 column, out int2 band)
    {
        bool known = bands.TryGetValue(column, out var b);
        band = b.xy;
        return known;
    }

    /// <summary>True when the chunk lies inside its column's band (or the band is not known yet).</summary>
    public bool InBand(int3 coord) => !bands.TryGetValue(coord.xz, out var b) || (coord.y >= b.x && coord.y <= b.y);

    void EnsureOffsets(int radius)
    {
        if (radius == offsetRadius)
            return;
        offsetRadius = radius;
        var list = new List<int2>();
        for (int z = -radius; z <= radius; z++)
        for (int x = -radius; x <= radius; x++)
            if (x * x + z * z <= radius * radius)
                list.Add(new int2(x, z));
        list.Sort((a, b) => math.lengthsq(a).CompareTo(math.lengthsq(b)));
        offsets = list.ToArray();
    }

    /// <summary>
    /// Requests bands for unknown columns within radius (nearest first) and queues the surface chunks
    /// of known columns that have not been queued yet. Every few ticks, queued columns are re-checked:
    /// needsGenerating(coord) says whether a chunk is in range but not loaded, generating or waiting,
    /// and such surface chunks are queued again (the flood fill brings back the rest).
    /// </summary>
    public void Tick(int2 center, int radius, int maxNewColumns, Action<int3> enqueue, Func<int3, bool> needsGenerating)
    {
        EnsureOffsets(radius);
        int requested = 0;
        bool busy = false;
        bool recheck = ++ticksSinceCheck >= RecheckEvery;
        if (recheck)
            ticksSinceCheck = 0;
        foreach (var o in offsets)
        {
            int2 column = center + o;
            if (expanded.Contains(column))
            {
                if (recheck && bands.TryGetValue(column, out var known))
                    for (int y = known.z; y <= known.w; y++)
                    {
                        var c = new int3(column.x, y, column.y);
                        if (needsGenerating(c))
                            enqueue(c);
                    }
                continue;
            }

            if (bands.TryGetValue(column, out var band))
            {
                for (int y = band.z; y <= band.w; y++)
                    enqueue(new int3(column.x, y, column.y));
                expanded.Add(column);
                continue;
            }

            busy = true;
            if (requested >= maxNewColumns || regions.IsColumnBandPending(column))
                continue;
            if (!regions.RequestColumnBand(column))
                break;   // throttled this frame
            requested++;
        }
        Busy = busy;
    }

    /// <summary>
    /// Forgets columns beyond radius so their chunks are queued again when the player comes back
    /// (the frontier drops chunks that far out). Bands are kept: they are small and stay valid.
    /// </summary>
    public void Prune(int2 center, float radius)
    {
        float r2 = radius * radius;
        expanded.RemoveWhere(c => math.lengthsq((float2)(c - center)) > r2);
        if (bands.Count > 65536)
        {
            float far2 = r2 * 4f;
            var drop = new List<int2>();
            foreach (var c in bands.Keys)
                if (math.lengthsq((float2)(c - center)) > far2)
                    drop.Add(c);
            foreach (var c in drop)
                bands.Remove(c);
        }
    }
}

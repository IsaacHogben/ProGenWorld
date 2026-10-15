using System;
using System.Collections.Generic;
using Unity.Mathematics;

/// <summary>
/// Region generator replacement for the flood fill: walks chunk columns nearest the player first,
/// asks RegionSystem for each column's height band, and queues only the chunks inside that band.
/// Chunks entirely above the terrain (open sky) or below it (buried rock) are never generated.
/// </summary>
public sealed class ColumnScheduler
{
    readonly int chunkSize;
    readonly RegionSystem regions;

    readonly Dictionary<int2, int2> bands = new();   // column -> chunk y range (inclusive)
    readonly HashSet<int2> expanded = new();         // columns whose band chunks were queued
    int2[] offsets = Array.Empty<int2>();            // column offsets within range, nearest first
    int offsetRadius = -1;

    int minChunkY = int.MaxValue, maxChunkY = int.MinValue;

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

    void HandleBand(int2 column, float minY, float maxY)
    {
        // Chunks share their top face with the chunk above (chunkSize + 1 samples), so a surface
        // exactly on a boundary is in the lower chunk
        int lo = (int)math.floor((minY - 1f) / chunkSize);
        int hi = (int)math.floor(maxY / chunkSize);
        bands[column] = new int2(lo, hi);
        minChunkY = math.min(minChunkY, lo);
        maxChunkY = math.max(maxChunkY, hi);
    }

    /// <summary>False while the column's band is unknown: its chunks may still hold terrain.</summary>
    public bool IsColumnKnown(int2 column) => bands.ContainsKey(column);

    public bool TryGetBand(int2 column, out int2 band) => bands.TryGetValue(column, out band);

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
    /// Requests bands for unknown columns within radius (nearest first) and queues the chunks of
    /// known columns that have not been queued yet. enqueue gets every chunk coord in a column's band.
    /// </summary>
    public void Tick(int2 center, int radius, int maxNewColumns, Action<int3> enqueue)
    {
        EnsureOffsets(radius);
        int requested = 0;
        bool busy = false;
        foreach (var o in offsets)
        {
            int2 column = center + o;
            if (expanded.Contains(column))
                continue;

            if (bands.TryGetValue(column, out var band))
            {
                for (int y = band.x; y <= band.y; y++)
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

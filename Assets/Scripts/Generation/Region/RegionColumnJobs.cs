// RegionColumnJobs.cs
// The region generator's 2D layer: which biome regions cover a point and how much (warped Voronoi
// cells, each one biome picked from the climate at its centre, blended over borderBlend), the
// blended surface height, and per column data for density, blocks and chunk scheduling.

using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>Up to three biomes covering a point, weights summing to 1, heaviest first.</summary>
public struct RegionWeights
{
    public byte b0, b1, b2;
    public float w0, w1, w2;
}

/// <summary>Job safe region settings and noises (built by RegionSystem from RegionGenSettingsSO).</summary>
public struct RegionParams
{
    public uint seed;
    public float regionSize;
    public float jitter;
    public float warpAmplitude;
    public float invBlend;
    public FbmSimplexNoise2D warpX, warpZ;

    public FbmSimplexNoise2D temperature, humidity;
    public float climateContrast;
    public int humidityDivisions, temperatureDivisions;
    public byte fallbackBiome;

    public float macroBase, macroAmplitude;
    public FbmSimplexNoise2D macro;

    public float paletteEdge;
    public FbmSimplexNoise2D paletteNoise;
}

/// <summary>Shared region math, callable from Burst jobs.</summary>
public static class RegionMath
{
    public static RegionWeights Weights(float2 pos, in RegionParams rp,
        in NativeArray<BiomeShapeData> shapes, in NativeArray<ClimateGridCell> climateCells,
        in NativeArray<byte> cellBiomes, in NativeArray<float> rarity)
    {
        // Bend the borders, then find the three nearest region centres
        float2 q = pos + rp.warpAmplitude * new float2(rp.warpX.GetNoise(pos.x, pos.y), rp.warpZ.GetNoise(pos.x, pos.y));
        float2 c = q / rp.regionSize;
        int2 ci = (int2)math.floor(c);

        float d0 = float.MaxValue, d1 = float.MaxValue, d2 = float.MaxValue;
        int2 c0 = default, c1 = default, c2 = default;
        for (int dz = -2; dz <= 2; dz++)
        for (int dx = -2; dx <= 2; dx++)
        {
            int2 cell = ci + new int2(dx, dz);
            float d = math.distancesq(c, CellCentre(cell, rp));
            if (d < d0) { d2 = d1; c2 = c1; d1 = d0; c1 = c0; d0 = d; c0 = cell; }
            else if (d < d1) { d2 = d1; c2 = c1; d1 = d; c1 = cell; }
            else if (d < d2) { d2 = d; c2 = cell; }
        }
        d0 = math.sqrt(d0) * rp.regionSize;
        d1 = math.sqrt(d1) * rp.regionSize;
        d2 = math.sqrt(d2) * rp.regionSize;

        // Weight by how much further a centre is than the nearest one: equal on the border,
        // zero borderBlend blocks of distance difference away (about half that from the border)
        float w1 = math.smoothstep(0f, 1f, 1f - (d1 - d0) * rp.invBlend);
        float w2 = math.smoothstep(0f, 1f, 1f - (d2 - d0) * rp.invBlend);

        var rw = new RegionWeights { b0 = BiomeForCell(c0, rp, shapes, climateCells, cellBiomes, rarity), w0 = 1f };
        rw.b1 = rw.b0; rw.b2 = rw.b0;
        if (w1 > 0f)
            Add(ref rw, BiomeForCell(c1, rp, shapes, climateCells, cellBiomes, rarity), w1);
        if (w2 > 0f)
            Add(ref rw, BiomeForCell(c2, rp, shapes, climateCells, cellBiomes, rarity), w2);

        float inv = 1f / (rw.w0 + rw.w1 + rw.w2);
        rw.w0 *= inv; rw.w1 *= inv; rw.w2 *= inv;
        Sort(ref rw);
        return rw;
    }

    // Neighbouring regions of the same biome merge into one weight
    static void Add(ref RegionWeights rw, byte biome, float w)
    {
        if (biome == rw.b0) rw.w0 += w;
        else if (rw.w1 > 0f && biome == rw.b1) rw.w1 += w;
        else if (rw.w1 == 0f) { rw.b1 = biome; rw.w1 = w; }
        else { rw.b2 = biome; rw.w2 = w; }
    }

    static void Sort(ref RegionWeights rw)
    {
        if (rw.w1 > rw.w0) { (rw.b0, rw.b1) = (rw.b1, rw.b0); (rw.w0, rw.w1) = (rw.w1, rw.w0); }
        if (rw.w2 > rw.w1) { (rw.b1, rw.b2) = (rw.b2, rw.b1); (rw.w1, rw.w2) = (rw.w2, rw.w1); }
        if (rw.w1 > rw.w0) { (rw.b0, rw.b1) = (rw.b1, rw.b0); (rw.w0, rw.w1) = (rw.w1, rw.w0); }
    }

    // Region centre in region units
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float2 CellCentre(int2 cell, in RegionParams rp)
    {
        uint h = Hash(cell, rp.seed);
        float2 r = new float2(h & 0xFFFF, h >> 16) * (1f / 65535f) - 0.5f;
        return (float2)cell + 0.5f + r * rp.jitter;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Hash(int2 cell, uint seed)
    {
        uint h = (uint)cell.x * 0x8da6b343u ^ (uint)cell.y * 0xd8163841u ^ seed * 0xcb1ab31fu;
        h ^= h >> 16;
        h *= 0x7feb352du;
        h ^= h >> 15;
        h *= 0x846ca68bu;
        h ^= h >> 16;
        return h;
    }

    // A region's biome: from the climate grid cell at its centre, by rarity weight
    static byte BiomeForCell(int2 cell, in RegionParams rp,
        in NativeArray<BiomeShapeData> shapes, in NativeArray<ClimateGridCell> climateCells,
        in NativeArray<byte> cellBiomes, in NativeArray<float> rarity)
    {
        float2 centre = CellCentre(cell, rp) * rp.regionSize;
        float t = math.saturate(0.5f + 0.5f * rp.climateContrast * rp.temperature.GetNoise(centre.x, centre.y));
        float hu = math.saturate(0.5f + 0.5f * rp.climateContrast * rp.humidity.GetNoise(centre.x, centre.y));
        int hi = math.clamp((int)(hu * rp.humidityDivisions), 0, rp.humidityDivisions - 1);
        int ti = math.clamp((int)(t * rp.temperatureDivisions), 0, rp.temperatureDivisions - 1);
        float roll = (Hash(cell, rp.seed ^ 0x5bd1e995u) & 0xFFFFFF) * (1f / 16777216f);

        if (TryPick(hi, ti, roll, rp, shapes, climateCells, cellBiomes, rarity, out byte biome))
            return biome;

        // Empty climate cell: nearest cell with a defined biome
        int maxRadius = math.max(rp.humidityDivisions, rp.temperatureDivisions);
        for (int r = 1; r <= maxRadius; r++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (math.abs(dx) != r && math.abs(dy) != r)
                continue;
            int h2 = hi + dx, t2 = ti + dy;
            if (h2 < 0 || t2 < 0 || h2 >= rp.humidityDivisions || t2 >= rp.temperatureDivisions)
                continue;
            if (TryPick(h2, t2, roll, rp, shapes, climateCells, cellBiomes, rarity, out biome))
                return biome;
        }
        return rp.fallbackBiome;
    }

    static bool TryPick(int hi, int ti, float roll, in RegionParams rp,
        in NativeArray<BiomeShapeData> shapes, in NativeArray<ClimateGridCell> climateCells,
        in NativeArray<byte> cellBiomes, in NativeArray<float> rarity, out byte biome)
    {
        biome = 0;
        int index = ti * rp.humidityDivisions + hi;
        if (index >= climateCells.Length)
            return false;
        var cc = climateCells[index];

        float total = 0f;
        for (int i = 0; i < cc.biomeCount; i++)
        {
            byte b = cellBiomes[cc.biomeStartIndex + i];
            if (b < shapes.Length && shapes[b].defined != 0)
                total += b < rarity.Length ? rarity[b] : 1f;
        }
        if (total <= 0f)
            return false;

        float target = roll * total, cumulative = 0f;
        for (int i = 0; i < cc.biomeCount; i++)
        {
            byte b = cellBiomes[cc.biomeStartIndex + i];
            if (b >= shapes.Length || shapes[b].defined == 0)
                continue;
            biome = b;
            cumulative += b < rarity.Length ? rarity[b] : 1f;
            if (target < cumulative)
                break;
        }
        return true;
    }

    /// <summary>A biome's own surface height at a point (before blending).</summary>
    public static float BiomeHeight(in BiomeShapeData s, float x, float z)
    {
        float n = s.hillNoise.GetNoise(x, z);
        if (s.ridged > 0f)
        {
            float r = 1f - math.abs(s.ridgeNoise.GetNoise(x, z));
            n = math.lerp(n, r * r * 2f - 1f, s.ridged);
        }
        float h = s.baseHeight + s.hillHeight * n;

        if (s.terraceStep > 0f)
        {
            // Flat steps that rise ever more steeply into the next one
            float t = h / s.terraceStep;
            float fl = math.floor(t);
            h = (fl + math.pow(t - fl, s.terraceExponent)) * s.terraceStep;
        }
        return h;
    }

    public static float Height(float2 pos, in RegionWeights rw, in RegionParams rp, in NativeArray<BiomeShapeData> shapes)
    {
        float h = rp.macroBase + rp.macroAmplitude * rp.macro.GetNoise(pos.x, pos.y);
        h += rw.w0 * BiomeHeight(shapes[rw.b0], pos.x, pos.y);
        if (rw.w1 > 0f) h += rw.w1 * BiomeHeight(shapes[rw.b1], pos.x, pos.y);
        if (rw.w2 > 0f) h += rw.w2 * BiomeHeight(shapes[rw.b2], pos.x, pos.y);
        return h;
    }
}

/// <summary>
/// Region weights and surface height on a grid with a one point border (for slopes):
/// point (i, j) of rawSide^2 sits at origin + (i - 1, j - 1) * spacing.
/// </summary>
[BurstCompile]
public struct RegionRawJob : IJobParallelFor
{
    public float2 origin;
    public float spacing;
    public int rawSide;
    public RegionParams rp;

    [ReadOnly] public NativeArray<BiomeShapeData> shapes;
    [ReadOnly] public NativeArray<ClimateGridCell> climateCells;
    [ReadOnly] public NativeArray<byte> cellBiomes;
    [ReadOnly] public NativeArray<float> rarity;

    [WriteOnly] public NativeArray<float> heights;
    [WriteOnly] public NativeArray<RegionWeights> weights;

    public void Execute(int index)
    {
        int i = index % rawSide, j = index / rawSide;
        float2 pos = origin + new float2(i - 1, j - 1) * spacing;
        var rw = RegionMath.Weights(pos, rp, shapes, climateCells, cellBiomes, rarity);
        weights[index] = rw;
        heights[index] = RegionMath.Height(pos, rw, rp, shapes);
    }
}

/// <summary>
/// Turns the raw grid into ColumnSamples (side^2, x fastest) and, when hints has room, the
/// BiomeHint grid the block assignment and decoration jobs read (primaryBiome = palette).
/// </summary>
[BurstCompile]
public struct RegionColumnJob : IJobParallelFor
{
    public float2 origin;
    public float spacing;
    public int side;
    public RegionParams rp;

    [ReadOnly] public NativeArray<BiomeShapeData> shapes;
    [ReadOnly] public NativeArray<float> heights;
    [ReadOnly] public NativeArray<RegionWeights> weights;

    [WriteOnly] public NativeArray<ColumnSample> columns;
    [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<BiomeHint> hints;

    public void Execute(int index)
    {
        int x = index % side, z = index / side;
        int rawSide = side + 2;
        int r = (x + 1) + (z + 1) * rawSide;

        float h = heights[r];
        float gx = (heights[r + 1] - heights[r - 1]) / (2f * spacing);
        float gz = (heights[r + rawSide] - heights[r - rawSide]) / (2f * spacing);

        var rw = weights[r];
        float2 pos = origin + new float2(x, z) * spacing;

        var col = new ColumnSample
        {
            height = h,
            invNorm = math.rsqrt(1f + gx * gx + gz * gz),
            bandMin = h,
            bandMax = h,
            palette = Palette(rw, pos),
            detail0 = ColumnSample.None,
            detail1 = ColumnSample.None,
            detail2 = ColumnSample.None
        };

        AddDetail(ref col, rw.b0, rw.w0);
        if (rw.w1 > 0f) AddDetail(ref col, rw.b1, rw.w1);
        if (rw.w2 > 0f) AddDetail(ref col, rw.b2, rw.w2);

        columns[index] = col;
        if (index < hints.Length)
            hints[index] = new BiomeHint { primaryBiome = col.palette, secondaryBiome = col.palette };
    }

    // The heaviest biome, with the line where two weights cross bent by noise (per biome, so it
    // does not jump when the order of the weights changes)
    byte Palette(in RegionWeights rw, float2 pos)
    {
        if (rw.w1 <= 0f || rp.paletteEdge <= 0f)
            return rw.b0;

        byte best = rw.b0;
        float bestScore = rw.w0 + rp.paletteEdge * EdgeNoise(rw.b0, pos);
        float s1 = rw.w1 + rp.paletteEdge * EdgeNoise(rw.b1, pos);
        if (s1 > bestScore) { best = rw.b1; bestScore = s1; }
        if (rw.w2 > 0f && rw.w2 + rp.paletteEdge * EdgeNoise(rw.b2, pos) > bestScore)
            best = rw.b2;
        return best;
    }

    float EdgeNoise(byte biome, float2 pos) => rp.paletteNoise.GetNoise(pos.x + biome * 7919f, pos.y - biome * 6563f);

    void AddDetail(ref ColumnSample col, byte biome, float weight)
    {
        var s = shapes[biome];
        float w = weight * s.detailStrength;
        if (s.hasDetail == 0 || w < 0.001f)
            return;

        if (col.detail0 == ColumnSample.None) { col.detail0 = biome; col.detailW0 = w; }
        else if (col.detail1 == ColumnSample.None) { col.detail1 = biome; col.detailW1 = w; }
        else { col.detail2 = biome; col.detailW2 = w; }

        col.bandMin = math.min(col.bandMin, col.height - s.detailRangeDown);
        col.bandMax = math.max(col.bandMax, col.height + s.detailRangeUp);
    }
}

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
    public FbmSimplexNoise2D macro;          // broad rises and dips (rivers are planned on these)
    public FbmSimplexNoise2D macroDetail;    // smaller bumps on them, flattened near rivers like hills
    public float macroDetailAmplitude;

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

    /// <summary>Mask (0..1) of a sub-biome layer at a point.</summary>
    public static float SubMask(in SubBiomeData l, float x, float z)
    {
        if (l.kind == (byte)SubBiomeKind.Continuous)
        {
            float n = l.noise.GetNoise(x, z);
            float half = l.softness * 0.5f;
            return math.smoothstep(l.threshold - half, l.threshold + half, n);
        }

        // Patches: at most one rounded, wobbly patch per cell
        float2 p = new float2(x, z) * l.invScale;
        int2 ci = (int2)math.floor(p);
        float wobble = 0.35f * l.noise.GetNoise(x, z);
        float mask = 0f;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int2 cell = ci + new int2(dx, dz);
            uint h = Hash(cell, l.seed);
            if ((h & 0xFFFF) * (1f / 65535f) >= l.coverage)
                continue;
            uint h2 = Hash(cell, l.seed ^ 0x68E31DA4u);
            float2 centre = (float2)cell + 0.5f + (new float2(h2 & 0xFFFF, h2 >> 16) * (1f / 65535f) - 0.5f) * 0.5f;
            float radius = 0.25f + 0.2f * ((h >> 16) * (1f / 65535f));
            float d = math.distance(p, centre) / radius + wobble;
            mask = math.max(mask, 1f - math.smoothstep(1f - l.softness, 1f, d));
        }
        return mask;
    }

    /// <summary>A biome's own surface height at a point (before blending).</summary>
    public static float BiomeHeight(in BiomeShapeData s, float x, float z, float hillMultiplier)
    {
        float n = s.hillNoise.GetNoise(x, z);
        if (s.ridged > 0f)
        {
            float r = 1f - math.abs(s.ridgeNoise.GetNoise(x, z));
            n = math.lerp(n, r * r * 2f - 1f, s.ridged);
        }
        float h = s.baseHeight + s.hillHeight * hillMultiplier * n;

        if (s.terraceStep > 0f)
        {
            // Flat steps that rise ever more steeply into the next one
            float t = h / s.terraceStep;
            float fl = math.floor(t);
            h = (fl + math.pow(t - fl, s.terraceExponent)) * s.terraceStep;
        }
        return h;
    }

    /// <summary>One biome's height and sub-biome effects at a point.</summary>
    public struct BiomePoint
    {
        public float height;
        public float decoration;     // decoration chance multiplier
        public float detail;         // 3D shape strength multiplier
        public byte surfaceBlock;    // strongest sub-biome surface block (0 = none)
        public float surfaceMask;
        public byte detailLayer;     // strongest sub-biome with its own 3D shape (its detail source; None = none)
        public float detailMask;     // its mask: its shape takes over from the biome's by this much
    }

    public static BiomePoint SampleBiome(in BiomeShapeData s, in NativeArray<SubBiomeData> subs, float x, float z, float hillFactor)
    {
        var bp = new BiomePoint { decoration = 1f, detail = 1f, detailLayer = RegionDetailSources.None };
        float hillMul = 1f, offset = 0f;
        for (int i = s.subStart; i < s.subStart + s.subCount; i++)
        {
            var l = subs[i];
            float m = SubMask(l, x, z);
            if (m <= 0f)
                continue;
            offset += m * l.heightOffset;
            hillMul *= math.lerp(1f, l.hillMultiplier, m);
            bp.detail *= math.lerp(1f, l.detailMultiplier, m);
            bp.decoration *= math.lerp(1f, l.decorationMultiplier, m);
            if (l.surfaceBlock != 0 && m > bp.surfaceMask)
            {
                bp.surfaceMask = m;
                bp.surfaceBlock = l.surfaceBlock;
            }
            if (l.detailSource != RegionDetailSources.None && m > bp.detailMask)
            {
                bp.detailMask = m;
                bp.detailLayer = l.detailSource;
            }
        }
        bp.height = BiomeHeight(s, x, z, hillMul * hillFactor) + offset;
        return bp;
    }

    /// <summary>Blended surface (before rivers) and sub-biome effects at a point.</summary>
    public struct PointSample
    {
        public float height;
        public float decoration;
        public float detail0, detail1, detail2;   // 3D strength multipliers per weight slot
        public byte layer0, layer1, layer2;       // per weight slot: sub-biome 3D shape source (None = none)
        public float layerMask0, layerMask1, layerMask2;
        public byte surfaceBlock;
        public float surfaceScore;
    }

    /// <param name="hillFactor">1 = full hills, 0 = the ground without hills (rivers are planned on it).</param>
    public static PointSample Sample(float2 pos, in RegionWeights rw, in RegionParams rp,
        in NativeArray<BiomeShapeData> shapes, in NativeArray<SubBiomeData> subs, float hillFactor)
    {
        float macro = rp.macroBase + rp.macroAmplitude * rp.macro.GetNoise(pos.x, pos.y);
        if (hillFactor > 0f)
            macro += hillFactor * rp.macroDetailAmplitude * rp.macroDetail.GetNoise(pos.x, pos.y);
        var ps = new PointSample
        {
            height = macro, detail1 = 1f, detail2 = 1f,
            layer1 = RegionDetailSources.None, layer2 = RegionDetailSources.None
        };

        var b0 = SampleBiome(shapes[rw.b0], subs, pos.x, pos.y, hillFactor);
        ps.height += rw.w0 * b0.height;
        ps.decoration = rw.w0 * b0.decoration;
        ps.detail0 = b0.detail;
        ps.layer0 = b0.detailLayer;
        ps.layerMask0 = b0.detailMask;
        Surface(ref ps, b0, rw.w0);
        if (rw.w1 > 0f)
        {
            var b1 = SampleBiome(shapes[rw.b1], subs, pos.x, pos.y, hillFactor);
            ps.height += rw.w1 * b1.height;
            ps.decoration += rw.w1 * b1.decoration;
            ps.detail1 = b1.detail;
            ps.layer1 = b1.detailLayer;
            ps.layerMask1 = b1.detailMask;
            Surface(ref ps, b1, rw.w1);
        }
        if (rw.w2 > 0f)
        {
            var b2 = SampleBiome(shapes[rw.b2], subs, pos.x, pos.y, hillFactor);
            ps.height += rw.w2 * b2.height;
            ps.decoration += rw.w2 * b2.decoration;
            ps.detail2 = b2.detail;
            ps.layer2 = b2.detailLayer;
            ps.layerMask2 = b2.detailMask;
            Surface(ref ps, b2, rw.w2);
        }
        return ps;
    }

    static void Surface(ref PointSample ps, in BiomePoint bp, float weight)
    {
        float score = bp.surfaceMask * weight;
        if (bp.surfaceBlock != 0 && score > ps.surfaceScore)
        {
            ps.surfaceScore = score;
            ps.surfaceBlock = bp.surfaceBlock;
        }
    }
}

/// <summary>Raw per point output of RegionRawJob.</summary>
public struct RawColumn
{
    public RegionWeights rw;
    public RegionMath.PointSample s;   // s.height is after rivers and lakes
    public float waterY;
    public float nearWater;
    public byte bank;
}

/// <summary>
/// Region weights, sub-biomes, surface height and water on a grid with a one point border (for
/// slopes): point (i, j) of rawSide^2 sits at origin + (i - 1, j - 1) * spacing.
/// </summary>
[BurstCompile]
public struct RegionRawJob : IJobParallelFor
{
    public float2 origin;
    public float spacing;
    public int rawSide;
    public RegionParams rp;
    public float waterLevel;
    public float bankWidth;

    [ReadOnly] public NativeArray<BiomeShapeData> shapes;
    [ReadOnly] public NativeArray<SubBiomeData> subs;
    [ReadOnly] public NativeArray<ClimateGridCell> climateCells;
    [ReadOnly] public NativeArray<byte> cellBiomes;
    [ReadOnly] public NativeArray<float> rarity;

    // Rivers and lakes (RiverQuery's arrays)
    public float2 riverOrigin;
    public float riverBucketSize;
    public int2 riverBuckets;
    [ReadOnly] public NativeArray<RiverEdge> riverEdges;
    [ReadOnly] public NativeArray<RiverLake> riverLakes;
    [ReadOnly] public NativeArray<int2> riverBucketRanges;
    [ReadOnly] public NativeArray<int> riverItems;

    [WriteOnly] public NativeArray<RawColumn> output;

    public void Execute(int index)
    {
        int i = index % rawSide, j = index / rawSide;
        float2 pos = origin + new float2(i - 1, j - 1) * spacing;
        var raw = new RawColumn { waterY = waterLevel };
        raw.rw = RegionMath.Weights(pos, rp, shapes, climateCells, cellBiomes, rarity);

        var q = new RiverQuery
        {
            origin = riverOrigin,
            bucketSize = riverBucketSize,
            buckets = riverBuckets,
            edges = riverEdges,
            lakes = riverLakes,
            bucketRanges = riverBucketRanges,
            items = riverItems
        };
        raw.s = RegionMath.Sample(pos, raw.rw, rp, shapes, subs, RiverMath.HillFactor(pos, q));
        RiverMath.Apply(pos, q, bankWidth, ref raw.s.height, ref raw.waterY, out raw.nearWater, out bool bank);
        raw.bank = (byte)(bank ? 1 : 0);
        output[index] = raw;
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
    [ReadOnly] public NativeArray<DetailSourceData> sources;
    [ReadOnly] public NativeArray<RawColumn> raw;

    [WriteOnly] public NativeArray<ColumnSample> columns;
    [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<BiomeHint> hints;

    public void Execute(int index)
    {
        int x = index % side, z = index / side;
        int rawSide = side + 2;
        int r = (x + 1) + (z + 1) * rawSide;

        var c = raw[r];
        float h = c.s.height;
        float gx = (raw[r + 1].s.height - raw[r - 1].s.height) / (2f * spacing);
        float gz = (raw[r + rawSide].s.height - raw[r - rawSide].s.height) / (2f * spacing);

        var rw = c.rw;
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
            detail2 = ColumnSample.None,
            waterY = c.waterY,
            decoScale = c.s.decoration,
            surfaceOverride = c.s.surfaceScore > 0.5f ? c.s.surfaceBlock : (byte)0
        };

        // 3D shapes give way near water so overhangs do not hang over (or dam) rivers and lakes
        float keep = 1f - c.nearWater;
        AddBiomeDetail(ref col, rw.b0, rw.w0 * c.s.detail0 * keep, c.s.layer0, c.s.layerMask0);
        if (rw.w1 > 0f) AddBiomeDetail(ref col, rw.b1, rw.w1 * c.s.detail1 * keep, c.s.layer1, c.s.layerMask1);
        if (rw.w2 > 0f) AddBiomeDetail(ref col, rw.b2, rw.w2 * c.s.detail2 * keep, c.s.layer2, c.s.layerMask2);

        if (c.bank != 0 || h < c.waterY)
            col.surfaceOverride = shapes[col.palette].sedimentBlock;

        columns[index] = col;
        if (index < hints.Length)
            hints[index] = new BiomeHint
            {
                primaryBiome = col.palette,
                secondaryBiome = col.palette,
                waterY = col.waterY,
                decoScale = col.decoScale,
                surfaceOverride = col.surfaceOverride
            };
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

    // A biome's 3D shape, with the sub-biome layer that has its own shape here taking over by its mask
    void AddBiomeDetail(ref ColumnSample col, byte biome, float weight, byte layer, float layerMask)
    {
        float m = layer != RegionDetailSources.None ? layerMask : 0f;
        AddDetail(ref col, biome, weight * (1f - m));
        if (m > 0f)
            AddDetail(ref col, layer, weight * m);
    }

    void AddDetail(ref ColumnSample col, byte source, float weight)
    {
        var s = sources[source];
        float w = weight * s.strength;
        if (s.hasDetail == 0 || w < 0.001f)
            return;

        if (col.detail0 == ColumnSample.None) { col.detail0 = source; col.detailW0 = w; }
        else if (col.detail1 == ColumnSample.None) { col.detail1 = source; col.detailW1 = w; }
        else if (col.detail2 == ColumnSample.None) { col.detail2 = source; col.detailW2 = w; }
        else
        {
            // All three slots taken (biome borders with sub-biome shapes): the weakest gives way
            if (col.detailW2 <= col.detailW1 && col.detailW2 <= col.detailW0) { if (w <= col.detailW2) return; col.detail2 = source; col.detailW2 = w; }
            else if (col.detailW1 <= col.detailW0) { if (w <= col.detailW1) return; col.detail1 = source; col.detailW1 = w; }
            else { if (w <= col.detailW0) return; col.detail0 = source; col.detailW0 = w; }
        }

        col.bandMin = math.min(col.bandMin, col.height - s.rangeDown);
        col.bandMax = math.max(col.bandMax, col.height + s.rangeUp);
    }
}

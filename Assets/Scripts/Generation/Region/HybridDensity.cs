using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Region generator density: distance above the blended height surface (scaled by densityScale,
/// negative = solid), blended towards each biome's FastNoise 3D shape inside the column's band.
/// The shape trees are sampled with y measured from the surface, so a shape keeps sitting on the
/// ground as the height changes and shrinks into it as its biome's weight fades.
/// Thread safe after construction.
/// </summary>
public sealed class HybridDensity
{
    readonly int seed;
    readonly float densityScale;

    // Per detail source (RegionDetailSources: biome IDs, then sub-biome layers)
    readonly FastNoise[] noiseByBiome = new FastNoise[256];
    readonly float[] frequencyByBiome = new float[256];
    readonly float[] surfaceOffsetByBiome = new float[256];
    readonly float[] scaleByBiome = new float[256];
    readonly int[] spacingByBiome = new int[256];   // blocks between samples (the tree's Shape Sample Spacing)

    [ThreadStatic] static float[] tlsDetail, tlsX, tlsY, tlsZ, tlsOut, tlsNodes;

    public float DensityScale => densityScale;

    // Measuring: time inside the trees and samples taken, by detail level (sampleRes 1, 2, 4, 8...)
    public static readonly double[] TreeMs = new double[8];
    public static readonly long[] TreeSamples = new long[8];
    public static readonly long[] Fills = new long[8];
    static readonly object statsLock = new();

    void Gen(int id, float[] outs, int k, float[] xs, float[] ys, float[] zs, int sampleRes)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        noiseByBiome[id].GenPositionArray3D(outs, k, xs, ys, zs, seed);
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        int level = math.min(7, math.tzcnt(math.max(1, sampleRes)));
        lock (statsLock)
        {
            TreeMs[level] += ms;
            TreeSamples[level] += k;
        }
    }

    public HybridDensity(BiomeDataManager biomeDataManager, RegionGenSettingsSO settings, int seed, float frequency)
    {
        this.seed = seed;
        densityScale = settings.densityScale;

        var byTerrain = new Dictionary<TerrainTypeDefinitionSO, FastNoise>();
        void Register(int id, string owner, TerrainTypeDefinitionSO terrain, DetailCalibration.Placement placement, float densityScale)
        {
            if (!byTerrain.TryGetValue(terrain, out var noise))
            {
                try
                {
                    noise = string.IsNullOrEmpty(terrain.noiseNodeTree) ? null : FastNoise.FromEncodedNodeTree(terrain.noiseNodeTree);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[RegionGen] 3D shape tree of '{terrain.terrainTypeName}' failed: {e.Message}");
                }
                if (noise == null)
                    Debug.LogError($"[RegionGen] {owner}: 3D shape '{terrain.terrainTypeName}' has no usable noise tree; it is ignored.");
                byTerrain[terrain] = noise;
            }

            noiseByBiome[id] = noise;
            frequencyByBiome[id] = frequency / 100f * terrain.frequencyMultiplier;
            surfaceOffsetByBiome[id] = placement.ground;
            scaleByBiome[id] = densityScale;
            spacingByBiome[id] = math.max(1, (int)terrain.shapeSampleSpacing);
        }

        foreach (var biome in biomeDataManager.biomes)
        {
            var shape = biome != null ? biome.regionShape : null;
            if (shape?.detailTerrain != null && shape.detailStrength > 0f)
                Register(biome.BiomeID, $"Biome {biome.biomeType}", shape.detailTerrain, shape.ResolveDetail(frequency, seed),
                    shape.detailDensityScale);
        }

        int biomeCount = biomeDataManager.GetBiomeDefinitions().Length;
        foreach (var (biome, layer, index) in RegionDetailSources.Layers(biomeDataManager))
        {
            byte source = RegionDetailSources.LayerSource(biomeCount, layer, index);
            if (source != RegionDetailSources.None)
                Register(source, $"Biome {biome.biomeType}, layer '{layer.name}'", layer.detailTerrain, layer.ResolveDetail(frequency, seed),
                    layer.detailDensityScale);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    float SurfaceDensity(in ColumnSample c, float y) => densityScale * (y - c.height) * c.invNorm;

    // Per column of the chunk being filled, read once (thread static, reused)
    [ThreadStatic] static float[] tlsHeight, tlsInvNorm, tlsWeights, tlsColWeight, tlsWeightSum;
    [ThreadStatic] static int[] tlsBandLo, tlsBandHi;   // voxel y range of the 3D shape band in this chunk (lo > hi = none)
    [ThreadStatic] static byte[] tlsIds;                // the column's 4 detail sources (weights in tlsWeights)
    [ThreadStatic] static byte[] tlsNeeded;             // coarse grid points to sample

    static T[] Rent<T>(ref T[] buffer, int length)
    {
        if (buffer == null || buffer.Length < length)
            buffer = new T[Mathf.NextPowerOfTwo(length)];
        return buffer;
    }

    // A column's weight for one detail source (0 = not used there)
    static float WeightOf(byte[] ids, float[] weights, int col, int id)
    {
        int o = col * 4;
        if (ids[o] == id) return weights[o];
        if (ids[o + 1] == id) return weights[o + 1];
        if (ids[o + 2] == id) return weights[o + 2];
        if (ids[o + 3] == id) return weights[o + 3];
        return 0f;
    }

    /// <summary>
    /// Fills a chunk's density ((n + 1)^3, n = chunkSize / sampleRes, x fastest then y then z) from
    /// its full resolution column data ((chunkSize + 1)^2). The per voxel work runs in Burst
    /// (HybridDensityKernels); the trees are sampled in between.
    /// </summary>
    public unsafe void Fill(NativeArray<float> output, int3 chunkCoord, int chunkSize, int sampleRes, NativeArray<ColumnSample> columns)
    {
        int n = chunkSize / sampleRes;
        int side = n + 1;
        int colSide = chunkSize + 1;
        int count = side * side * side;
        int cols = side * side;
        int3 origin = chunkCoord * chunkSize;
        lock (statsLock)
            Fills[math.min(7, math.tzcnt(math.max(1, sampleRes)))]++;

        var height = Rent(ref tlsHeight, cols);
        var invNorm = Rent(ref tlsInvNorm, cols);
        var bandLo = Rent(ref tlsBandLo, cols);
        var bandHi = Rent(ref tlsBandHi, cols);
        var ids = Rent(ref tlsIds, cols * 4);
        var weights = Rent(ref tlsWeights, cols * 4);
        var colWeight = Rent(ref tlsColWeight, cols);
        var weightSum = Rent(ref tlsWeightSum, cols);

        // Each column's surface, 3D shape band and sources, read once
        Span<bool> used = stackalloc bool[256];
        bool anyDetail = false;
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int col = x + z * side;
            var c = columns[x * sampleRes + z * sampleRes * colSide];
            height[col] = c.height;
            invNorm[col] = c.invNorm;
            int o = col * 4;
            ids[o] = c.detail0; ids[o + 1] = c.detail1; ids[o + 2] = c.detail2; ids[o + 3] = c.detail3;
            weights[o] = c.detailW0; weights[o + 1] = c.detailW1; weights[o + 2] = c.detailW2; weights[o + 3] = c.detailW3;
            weightSum[col] = c.DetailWeight;

            int lo = 1, hi = 0;
            if (c.HasDetail)
            {
                lo = math.max(0, (int)math.ceil((c.bandMin - origin.y) / sampleRes));
                hi = math.min(n, (int)math.floor((c.bandMax - origin.y) / sampleRes));
                if (lo <= hi)
                {
                    anyDetail = true;
                    used[c.detail0] = true;
                    if (c.detail1 != ColumnSample.None) used[c.detail1] = true;
                    if (c.detail2 != ColumnSample.None) used[c.detail2] = true;
                    if (c.detail3 != ColumnSample.None) used[c.detail3] = true;
                }
            }
            bandLo[col] = lo;
            bandHi[col] = hi;
        }

        float* outPtr = (float*)NativeArrayUnsafeUtility.GetUnsafePtr(output);
        fixed (float* h = height, inv = invNorm)
            HybridDensityKernels.Surface(outPtr, h, inv, side, origin.y, sampleRes, densityScale);
        if (!anyDetail)
            return;

        var detail = Rent(ref tlsDetail, count);
        Array.Clear(detail, 0, count);
        var xs = Rent(ref tlsX, count);
        var ys = Rent(ref tlsY, count);
        var zs = Rent(ref tlsZ, count);
        var outs = Rent(ref tlsOut, count);
        var needed = Rent(ref tlsNeeded, count);
        var nodes = Rent(ref tlsNodes, count);

        fixed (float* pDetail = detail, pXs = xs, pYs = ys, pZs = zs, pOuts = outs, pNodes = nodes,
                      pHeight = height, pColWeight = colWeight, pWeightSum = weightSum)
        fixed (int* pLo = bandLo, pHi = bandHi)
        fixed (byte* pNeeded = needed)
        {
            for (int id = 0; id < 255; id++)
            {
                if (!used[id] || noiseByBiome[id] == null)
                    continue;

                for (int col = 0; col < cols; col++)
                    colWeight[col] = bandLo[col] > bandHi[col] ? 0f : WeightOf(ids, weights, col, id);

                float f = frequencyByBiome[id];
                float offset = surfaceOffsetByBiome[id];
                float scale = scaleByBiome[id];

                // Coarser than this detail level: sample a grid every `step` voxels and blend
                int step = spacingByBiome[id] / sampleRes;
                if (step >= 2 && n % step == 0)
                {
                    int kc = HybridDensityKernels.CoarsePositions(pNeeded, pXs, pYs, pZs, pColWeight, pLo, pHi, pHeight,
                        side, step, origin.x, origin.y, origin.z, sampleRes, f, offset);
                    if (kc == 0)
                        continue;
                    Gen(id, outs, kc, xs, ys, zs, sampleRes);
                    HybridDensityKernels.CoarseAccumulate(pDetail, pNodes, pNeeded, pOuts, pColWeight, pLo, pHi, side, step, scale);
                    continue;
                }

                int k = HybridDensityKernels.Positions(pXs, pYs, pZs, pColWeight, pLo, pHi, pHeight,
                    side, origin.x, origin.y, origin.z, sampleRes, f, offset);
                if (k == 0)
                    continue;
                Gen(id, outs, k, xs, ys, zs, sampleRes);
                HybridDensityKernels.Accumulate(pDetail, pOuts, pColWeight, pLo, pHi, side, scale);
            }

            HybridDensityKernels.Blend(outPtr, pDetail, pWeightSum, pLo, pHi, side);
        }
    }

    static float Weight(in ColumnSample c, byte id)
    {
        if (c.detail0 == id) return c.detailW0;
        if (c.detail1 == id) return c.detailW1;
        if (c.detail2 == id) return c.detailW2;
        if (c.detail3 == id) return c.detailW3;
        return 0f;
    }

    /// <summary>
    /// Density at count heights per column (yAt(column, i); NaN skips a point), for the far horizon.
    /// Result is column major: [column * count + i].
    /// </summary>
    public float[] Sample(ColumnSample[] columns, float[] worldX, float[] worldZ, int count, Func<int, int, float> yAt)
    {
        int total = columns.Length * count;
        var result = new float[total];
        var detail = new float[total];

        Span<bool> used = stackalloc bool[256];
        for (int col = 0; col < columns.Length; col++)
        {
            var c = columns[col];
            for (int i = 0; i < count; i++)
            {
                float y = yAt(col, i);
                result[col * count + i] = float.IsNaN(y) ? 0f : SurfaceDensity(c, y);
            }
            if (c.HasDetail)
            {
                used[c.detail0] = true;
                if (c.detail1 != ColumnSample.None) used[c.detail1] = true;
                if (c.detail2 != ColumnSample.None) used[c.detail2] = true;
                if (c.detail3 != ColumnSample.None) used[c.detail3] = true;
            }
        }

        var xs = new List<float>();
        var ys = new List<float>();
        var zs = new List<float>();
        var targets = new List<int>();
        for (int id = 0; id < 255; id++)
        {
            if (!used[id] || noiseByBiome[id] == null)
                continue;
            float f = frequencyByBiome[id];
            float offset = surfaceOffsetByBiome[id];
            xs.Clear(); ys.Clear(); zs.Clear(); targets.Clear();

            for (int col = 0; col < columns.Length; col++)
            {
                var c = columns[col];
                if (!c.Uses(id))
                    continue;
                for (int i = 0; i < count; i++)
                {
                    float y = yAt(col, i);
                    if (float.IsNaN(y) || y < c.bandMin || y > c.bandMax)
                        continue;
                    xs.Add(worldX[col] * f);
                    ys.Add((y - c.height + offset) * f);
                    zs.Add(worldZ[col] * f);
                    targets.Add(col * count + i);
                }
            }
            if (targets.Count == 0)
                continue;

            var outs = new float[targets.Count];
            noiseByBiome[id].GenPositionArray3D(outs, targets.Count, xs.ToArray(), ys.ToArray(), zs.ToArray(), seed);
            float scale = scaleByBiome[id];
            for (int i = 0; i < targets.Count; i++)
            {
                int t = targets[i];
                detail[t] += Weight(columns[t / count], (byte)id) * scale * outs[i];
            }
        }

        for (int col = 0; col < columns.Length; col++)
        {
            var c = columns[col];
            if (!c.HasDetail)
                continue;
            float w = math.min(1f, c.DetailWeight);
            for (int i = 0; i < count; i++)
            {
                float y = yAt(col, i);
                if (float.IsNaN(y) || y < c.bandMin || y > c.bandMax)
                    continue;
                int t = col * count + i;
                result[t] = (1f - w) * result[t] + detail[t];
            }
        }

        return result;
    }
}

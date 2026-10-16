using System;
using System.Collections.Generic;
using Unity.Collections;
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

    [ThreadStatic] static float[] tlsDetail, tlsX, tlsY, tlsZ, tlsOut;
    [ThreadStatic] static int[] tlsTargets;

    public float DensityScale => densityScale;

    public HybridDensity(BiomeDataManager biomeDataManager, RegionGenSettingsSO settings, int seed, float frequency)
    {
        this.seed = seed;
        densityScale = settings.densityScale;

        var byTerrain = new Dictionary<TerrainTypeDefinitionSO, FastNoise>();
        void Register(int id, string owner, TerrainTypeDefinitionSO terrain, bool autoCalibrate, float surfaceOffset, float densityScale)
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
            surfaceOffsetByBiome[id] = surfaceOffset;
            if (autoCalibrate)
            {
                var cal = DetailCalibration.Get(terrain, frequency, seed);
                if (cal.valid)
                    surfaceOffsetByBiome[id] = cal.ground;
            }
            scaleByBiome[id] = densityScale;
        }

        foreach (var biome in biomeDataManager.biomes)
        {
            var shape = biome != null ? biome.regionShape : null;
            if (shape?.detailTerrain != null && shape.detailStrength > 0f)
                Register(biome.BiomeID, $"Biome {biome.biomeType}", shape.detailTerrain, shape.autoCalibrate,
                    shape.detailSurfaceOffset, shape.detailDensityScale);
        }

        int biomeCount = biomeDataManager.GetBiomeDefinitions().Length;
        foreach (var (biome, layer, index) in RegionDetailSources.Layers(biomeDataManager))
        {
            byte source = RegionDetailSources.LayerSource(biomeCount, layer, index);
            if (source != RegionDetailSources.None)
                Register(source, $"Biome {biome.biomeType}, layer '{layer.name}'", layer.detailTerrain, layer.autoCalibrate,
                    layer.detailSurfaceOffset, layer.detailDensityScale);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    float SurfaceDensity(in ColumnSample c, float y) => densityScale * (y - c.height) * c.invNorm;

    static float[] Rent(ref float[] buffer, int length)
    {
        if (buffer == null || buffer.Length < length)
            buffer = new float[Mathf.NextPowerOfTwo(length)];
        return buffer;
    }

    /// <summary>
    /// Fills a chunk's density ((n + 1)^3, n = chunkSize / sampleRes, x fastest then y then z) from
    /// its full resolution column data ((chunkSize + 1)^2).
    /// </summary>
    public void Fill(NativeArray<float> output, int3 chunkCoord, int chunkSize, int sampleRes, NativeArray<ColumnSample> columns)
    {
        int n = chunkSize / sampleRes;
        int side = n + 1;
        int colSide = chunkSize + 1;
        int count = side * side * side;
        int3 origin = chunkCoord * chunkSize;

        // Height surface everywhere; note which 3D shape biomes reach into the chunk
        Span<bool> used = stackalloc bool[256];
        bool anyDetail = false;
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            var c = columns[x * sampleRes + z * sampleRes * colSide];
            int baseIndex = x + z * side * side;
            for (int y = 0; y < side; y++)
                output[baseIndex + y * side] = SurfaceDensity(c, origin.y + y * sampleRes);

            if (c.HasDetail && c.bandMax >= origin.y && c.bandMin <= origin.y + chunkSize)
            {
                anyDetail = true;
                used[c.detail0] = true;
                if (c.detail1 != ColumnSample.None) used[c.detail1] = true;
                if (c.detail2 != ColumnSample.None) used[c.detail2] = true;
            }
        }
        if (!anyDetail)
            return;

        var detail = Rent(ref tlsDetail, count);
        Array.Clear(detail, 0, count);
        var xs = Rent(ref tlsX, count);
        var ys = Rent(ref tlsY, count);
        var zs = Rent(ref tlsZ, count);
        var outs = Rent(ref tlsOut, count);
        if (tlsTargets == null || tlsTargets.Length < count)
            tlsTargets = new int[Mathf.NextPowerOfTwo(count)];
        var targets = tlsTargets;

        for (int id = 0; id < 255; id++)
        {
            if (!used[id] || noiseByBiome[id] == null)
                continue;

            float f = frequencyByBiome[id];
            float offset = surfaceOffsetByBiome[id];
            int k = 0;
            for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
            {
                var c = columns[x * sampleRes + z * sampleRes * colSide];
                if (c.detail0 != id && c.detail1 != id && c.detail2 != id)
                    continue;

                float wx = origin.x + x * sampleRes, wz = origin.z + z * sampleRes;
                int baseIndex = x + z * side * side;
                for (int y = 0; y < side; y++)
                {
                    float wy = origin.y + y * sampleRes;
                    if (wy < c.bandMin || wy > c.bandMax)
                        continue;
                    xs[k] = wx * f;
                    ys[k] = (wy - c.height + offset) * f;
                    zs[k] = wz * f;
                    targets[k] = baseIndex + y * side;
                    k++;
                }
            }
            if (k == 0)
                continue;

            noiseByBiome[id].GenPositionArray3D(outs, k, xs, ys, zs, seed);

            float scale = scaleByBiome[id];
            for (int i = 0; i < k; i++)
            {
                int t = targets[i];
                int x = t % side, z = t / (side * side);
                var c = columns[x * sampleRes + z * sampleRes * colSide];
                detail[t] += Weight(c, (byte)id) * scale * outs[i];
            }
        }

        // Blend: the 3D shapes replace the surface by their combined weight
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            var c = columns[x * sampleRes + z * sampleRes * colSide];
            if (!c.HasDetail)
                continue;
            float w = math.min(1f, c.DetailWeight);
            int baseIndex = x + z * side * side;
            for (int y = 0; y < side; y++)
            {
                float wy = origin.y + y * sampleRes;
                if (wy < c.bandMin || wy > c.bandMax)
                    continue;
                int i = baseIndex + y * side;
                output[i] = (1f - w) * output[i] + detail[i];
            }
        }
    }

    static float Weight(in ColumnSample c, byte id)
    {
        if (c.detail0 == id) return c.detailW0;
        if (c.detail1 == id) return c.detailW1;
        if (c.detail2 == id) return c.detailW2;
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
                if (c.detail0 != id && c.detail1 != id && c.detail2 != id)
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

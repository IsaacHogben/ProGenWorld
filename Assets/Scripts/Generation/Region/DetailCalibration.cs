using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Measures a FastNoise terrain tree used as a biome's 3D shape: where its ground sits (the
/// median height of the top of its solid base across many columns) and how far its shapes reach
/// above and below that. The region generator places that ground on the biome's surface and only
/// samples the tree within the measured reach. Results are cached per tree, frequency and seed.
/// </summary>
public static class DetailCalibration
{
    public struct Result
    {
        public bool valid;
        public float ground;   // tree y (blocks, before frequency) of its typical ground
        public float up;       // highest solid above the ground
        public float down;     // deepest air below the ground
    }

    /// <summary>Where a tree's ground sits (tree y) and how far it reaches above and below it (blocks).</summary>
    public struct Placement
    {
        public float ground, up, down;
    }

    /// <summary>
    /// A tree's placement: each value measured, unless overridden (or the tree cannot be measured,
    /// in which case the given values are used).
    /// </summary>
    public static Placement Resolve(TerrainTypeDefinitionSO terrain, float frequency, int seed,
        bool overrideGround, float ground, bool overrideUp, float up, bool overrideDown, float down)
    {
        var p = new Placement { ground = ground, up = up, down = down };
        if (terrain == null || (overrideGround && overrideUp && overrideDown))
            return p;
        var cal = Get(terrain, frequency, seed);
        if (!cal.valid)
            return p;
        if (!overrideGround) p.ground = cal.ground;
        if (!overrideUp) p.up = cal.up;
        if (!overrideDown) p.down = cal.down;
        return p;
    }

    const int Columns = 24;          // per side
    const float ColumnSpacing = 97f; // blocks
    const float YMin = -1024f, YMax = 1024f, YStep = 3f;

    static readonly Dictionary<(TerrainTypeDefinitionSO, float, int), Result> cache = new();

    public static Result Get(TerrainTypeDefinitionSO terrain, float frequency, int seed)
    {
        var key = (terrain, frequency, seed);
        lock (cache)
            if (cache.TryGetValue(key, out var cached))
                return cached;

        var result = Measure(terrain, frequency, seed);
        lock (cache)
            cache[key] = result;
        return result;
    }

    static Result Measure(TerrainTypeDefinitionSO terrain, float frequency, int seed)
    {
        FastNoise noise;
        try
        {
            noise = string.IsNullOrEmpty(terrain.noiseNodeTree) ? null : FastNoise.FromEncodedNodeTree(terrain.noiseNodeTree);
        }
        catch (Exception)
        {
            noise = null;
        }
        if (noise == null)
            return default;

        float f = frequency / 100f * terrain.frequencyMultiplier;
        int steps = (int)((YMax - YMin) / YStep) + 1;
        int count = Columns * Columns * steps;
        var xs = new float[count];
        var ys = new float[count];
        var zs = new float[count];
        int k = 0;
        for (int c = 0; c < Columns * Columns; c++)
        {
            float x = (c % Columns) * ColumnSpacing, z = (c / Columns) * ColumnSpacing;
            for (int s = 0; s < steps; s++, k++)
            {
                xs[k] = x * f;
                ys[k] = (YMax - s * YStep) * f;   // top down
                zs[k] = z * f;
            }
        }
        var d = new float[count];
        noise.GenPositionArray3D(d, count, xs, ys, zs, seed);

        // Per column: highest solid, and the top of the solid base (first air going up from the bottom)
        var bases = new List<float>();
        float highest = float.MinValue, lowestBase = float.MaxValue;
        int terrainLike = 0;
        for (int c = 0; c < Columns * Columns; c++)
        {
            int o = c * steps;
            if (d[o] <= 0f || d[o + steps - 1] > 0f)
                continue;   // solid at the top or open at the bottom: not ground here
            terrainLike++;

            for (int s = 0; s < steps; s++)
                if (d[o + s] <= 0f)
                {
                    highest = Mathf.Max(highest, YMax - s * YStep);
                    break;
                }
            for (int s = steps - 1; s >= 0; s--)
                if (d[o + s] > 0f)
                {
                    float top = YMax - (s + 1) * YStep;
                    bases.Add(top);
                    lowestBase = Mathf.Min(lowestBase, top);
                    break;
                }
        }

        if (terrainLike < Columns * Columns / 2 || bases.Count == 0)
        {
            Debug.LogWarning($"[RegionGen] Could not measure 3D shape '{terrain.terrainTypeName}': it is not solid below and open above within y {YMin}..{YMax} in most places. Its manual surface offset and ranges are used.");
            return default;
        }

        bases.Sort();
        float ground = bases[bases.Count / 2];
        float up = highest - ground, down = ground - lowestBase;
        var result = new Result
        {
            valid = true,
            ground = ground,
            up = up * 1.15f + 8f,
            down = down * 1.15f + 8f
        };
        Debug.Log($"[RegionGen] 3D shape '{terrain.terrainTypeName}': ground at tree y {ground:F0}, shapes reach {up:F0} above and {down:F0} below it.");
        return result;
    }
}

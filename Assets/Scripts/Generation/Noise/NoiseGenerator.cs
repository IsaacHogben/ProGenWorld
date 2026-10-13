// delve style
//noise = FastNoise.FromEncodedNodeTree("EAD2KNw/JQAAAIA/PQrXPwAAgD8AAIA/DQAEAAAAuB4FQAcAAAAAAD8AAAAAAAAK1yM+");
// delve with caps (glacial)
//noise = FastNoise.FromEncodedNodeTree("GgABHQAEAAAAAABI4bo/AAAAAAAAAAAAAAAAzcyMPwAAAAAAAAAAAAAAAAABGgABJQAAAIA/AADAPwAAgD8AAIA/DQAEAAAAH4XrPwcAAAAAAD8AAAAAAAEQAArXA0AeAAQAAAAAAAAA8EEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAK1yM9");
// Delve Original Crunchy
//EAAK1wNAGQAZABkAHQAEAAAAAAAzMxPAAAAAAAAAAAAAAAAACtejPQAAAAAAAAAAAAAAAAABHQAEAAAAAAAK17NAAAAAAAAAAAAAAAAAhetRPwAAAAAAAAAAAAAAAAABIQATAIXr0T8lAAAAgD8AAMA/AACAPwAAgD8NAAMAAADNzCxABwAAAAAAPwCF61E/AAAAAIA/AClcDz4BHgAEAAAAAAAAAPBBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACtcjPQ==
// Hills + Cubes
//noise = FastNoise.FromEncodedNodeTree("HgAEAAAAAAB7FO4/AAAAAAAAAAAAAAAAAAAAvwAAAAAAAAAAARsAEQACAAAAAAAgQBAAAAAAQBkAEwDD9Sg/DQAEAAAAAAAgQAkAAGZmJj8AAAAAPwEEAAAAAAAAAEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAM3MTD4AMzMzPwAfhWtAARoAARMAKVwvQAwABAAAAK5HYT7//wMAAEjhej8A7FG4vg==");
//noise = new FastNoise("FractalFBm");
// Nice Hills
//noise = FastNoise.FromEncodedNodeTree("EwDNzMw+EQACAAAAAAAAQBAAexSuPhkAGQATAB+F6z8lAAAAgD/D9eg/AACAPwAAgD8RAAQAAACuRwFAEAAK12NADQADAAAAAAAAQBAAAAAAPwcAAJqZmT4AKVwPPwDXo3A/AM3MzD0AzcyMPwAUrgdAAQAAj8J1PQEEAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHsULj8AMzMzPwAAAAA/");
//noise = FastNoise.FromEncodedNodeTree("BAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==");
// Terrain Type 0: Big Domain warp Hills
//noise = FastNoise.FromEncodedNodeTree("EQACAAAAAAAAQBAAexSuPhkAGQATAB+F6z8lAAAAgD/D9eg/AACAPwAAgD8RAAQAAACuRwFAEAAK12NADQADAAAAAAAAQBAAAAAAPwcAAJqZmT4AKVwPPwDXo3A/AM3MzD0AzcyMPwAUrgdAAQAAj8J1PQEEAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAOF6FEAAMzMzPwAAAAA/");
// Terrain Type 1: Delve with caps
//secondaryNoise = FastNoise.FromEncodedNodeTree("GgABHQAEAAAAAABI4bo/AAAAAAAAAAAAAAAAzcyMPwAAAAAAAAAAAAAAAAABGgABJQAAAIA/AADAPwAAgD8AAIA/DQAEAAAAH4XrPwcAAAAAAD8AAAAAAAEQAArXA0AeAAQAAAAAAAAA8EEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAK1yM9");
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Mathematics;

public class NoiseGenerator
{
    private int seed;

    // Cache of noise generators by terrain ID (thread-safe, read-only after init)
    private Dictionary<byte, FastNoise> noiseGenerators = new Dictionary<byte, FastNoise>();

    public NoiseGenerator(int inSeed)
    {
        seed = inSeed;
    }

    /// <summary>
    /// Initialize noise generators for all terrain types that have noise configured
    /// </summary>
    public void InitializeTerrainNoises(TerrainTypeDefinitionSO[] terrainTypes)
    {
        noiseGenerators.Clear();

        for (int i = 0; i < terrainTypes.Length; i++)
        {
            var terrain = terrainTypes[i];
            byte terrainID = terrain.TerrainID;

            UnityEngine.Debug.Log($"Processing terrain {i}: '{terrain.terrainTypeName}' with ID: {terrainID}");

            if (!string.IsNullOrEmpty(terrain.noiseNodeTree))
            {
                try
                {
                    FastNoise noise = FastNoise.FromEncodedNodeTree(terrain.noiseNodeTree);
                    noiseGenerators[terrainID] = noise;
                    UnityEngine.Debug.Log($"Successfully initialized noise for terrain '{terrain.terrainTypeName}' (ID: {terrainID})");
                }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogError($"Failed to create noise for terrain '{terrain.terrainTypeName}' (ID: {terrainID}): {e.Message}");
                }
            }
            else
            {
                UnityEngine.Debug.LogWarning($"Terrain '{terrain.terrainTypeName}' (ID: {terrainID}) has no noise node tree configured!");
            }
        }

        UnityEngine.Debug.Log($"NoiseGenerator initialized with {noiseGenerators.Count} terrain noise generators");
        UnityEngine.Debug.Log($"Available terrain IDs: {string.Join(", ", noiseGenerators.Keys)}");
    }

    // Scratch buffers for the per-terrain noise fields and the blended result. Reused across
    // chunks instead of allocating ~1 MB arrays per terrain type per chunk, which landed on the
    // large object heap and caused GC hitches.
    static readonly ConcurrentBag<float[]> scratchPool = new ConcurrentBag<float[]>();
    const int MaxPooledBuffers = 48;

    // Per thread lookup: terrain id -> noise field for the chunk being generated
    [ThreadStatic] static float[][] tlsFieldsById;
    [ThreadStatic] static bool[] tlsUsedIds;

    static float[] RentScratch(int length)
    {
        while (scratchPool.TryTake(out var buffer))
        {
            if (buffer.Length == length)
                return buffer;
        }
        return new float[length];
    }

    static void ReturnScratch(float[] buffer)
    {
        if (buffer != null && scratchPool.Count < MaxPooledBuffers)
            scratchPool.Add(buffer);
    }

    /// <summary>
    /// Generates density by blending between multiple terrain-specific noise generators,
    /// writing the result straight into <paramref name="output"/>.
    /// </summary>
    public void FillDensity(
        NativeArray<float> output,
        int3 chunkCoord,
        int chunkSize,
        float frequency,
        int sampleRes,
        BiomeData biome,
        TerrainTypeDefinitionSO[] terrainTypeSOs)
    {
        int scaledSize = chunkSize / sampleRes;
        int side = scaledSize + 1;
        int voxelCount = side * side * side;

        int3 worldPos = (chunkCoord * chunkSize) / sampleRes;
        float relativeFrequency = frequency / 100f * sampleRes;

        var fieldsById = tlsFieldsById ??= new float[256][];
        var usedIds = tlsUsedIds ??= new bool[256];
        Array.Clear(usedIds, 0, usedIds.Length);

        // Only generate noise for terrain types this chunk actually uses
        for (int i = 0; i < biome.grid.Length; i++)
        {
            BiomeHint hint = biome.grid[i];
            usedIds[hint.primaryTerrainType] = true;
            if (hint.terrainBlend > 0)
                usedIds[hint.secondaryTerrainType] = true;
        }

        float[] blended = RentScratch(voxelCount);
        try
        {
            for (int id = 0; id < 256; id++)
            {
                if (!usedIds[id])
                    continue;

                byte terrainID = (byte)id;
                if (!noiseGenerators.TryGetValue(terrainID, out FastNoise noise))
                {
                    UnityEngine.Debug.LogWarning($"Terrain ID {terrainID} used in chunk but no noise generator configured!");
                    continue;
                }

                // Terrain-specific settings
                TerrainTypeDefinitionSO terrainSO = terrainTypeSOs[terrainID];
                float terrainFrequency = relativeFrequency * terrainSO.frequencyMultiplier;
                int3 terrainWorldPos = worldPos;
                terrainWorldPos.y += (int)math.round(terrainSO.yOffset / (float)sampleRes);  // Apply Y offset (in samples; off by under half a sample at low resolution)

                float[] field = RentScratch(voxelCount);
                noise.GenUniformGrid3D(
                    field,
                    terrainWorldPos.x,
                    terrainWorldPos.y,
                    terrainWorldPos.z,
                    side,
                    side,
                    side,
                    terrainFrequency,
                    seed
                );
                fieldsById[id] = field;
            }

            BlendTerrainNoises(blended, fieldsById, chunkSize, sampleRes, side, biome);
            NativeArray<float>.Copy(blended, output, voxelCount);
        }
        finally
        {
            for (int id = 0; id < 256; id++)
            {
                if (fieldsById[id] == null)
                    continue;
                ReturnScratch(fieldsById[id]);
                fieldsById[id] = null;
            }
            ReturnScratch(blended);
        }
    }

    /// <summary>
    /// Blends between terrain-specific noise fields based on biome hints
    /// </summary>
    static void BlendTerrainNoises(
        float[] finalDensity,
        float[][] fieldsById,
        int chunkSize,
        int sampleRes,
        int side,
        BiomeData biome)
    {
        int scaledSize = chunkSize / sampleRes;
        int biomeRes = biome.resolution;
        int biomeSide = biomeRes + 1;
        float invScaledSize = 1f / scaledSize;

        for (int z = 0; z < side; z++)
        {
            float lz = z * invScaledSize;
            int bz = (int)math.round(lz * biomeRes);
            bz = math.min(bz, biomeRes);
            int bzOffset = bz * biomeSide;

            for (int x = 0; x < side; x++)
            {
                float lx = x * invScaledSize;
                int bx = (int)math.round(lx * biomeRes);
                bx = math.min(bx, biomeRes);

                BiomeHint hint = biome.grid[bx + bzOffset];
                float terrainBlendFactor = hint.terrainBlend * 0.00392156862745098f;

                // Look the fields up once per column instead of once per voxel
                float[] primary = fieldsById[hint.primaryTerrainType];
                float[] secondary = terrainBlendFactor > 0.001f ? fieldsById[hint.secondaryTerrainType] : null;

                if (primary == null)
                    UnityEngine.Debug.LogError($"Primary terrain ID {hint.primaryTerrainType} has no noise generator!");

                int columnBaseIdx = x + z * side * side;

                for (int y = 0; y < side; y++)
                {
                    int idx = columnBaseIdx + y * side;
                    float primaryValue = primary != null ? primary[idx] : 0f;
                    finalDensity[idx] = secondary != null
                        ? primaryValue + (secondary[idx] - primaryValue) * terrainBlendFactor
                        : primaryValue;
                }
            }
        }
    }
}
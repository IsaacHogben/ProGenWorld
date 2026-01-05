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

    /// <summary>
    /// Generates density by blending between multiple terrain-specific noise generators
    /// </summary>
    public float[] FillDensity(
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

        // OPTIMIZATION: First, scan biome hints to find which terrain types are actually used
        HashSet<byte> usedTerrainIDs = new HashSet<byte>();
        int biomeRes = biome.resolution;
        int biomeSide = biomeRes + 1;

        for (int i = 0; i < biome.grid.Length; i++)
        {
            BiomeHint hint = biome.grid[i];
            usedTerrainIDs.Add(hint.primaryTerrainType);
            if (hint.terrainBlend > 0)
            {
                usedTerrainIDs.Add(hint.secondaryTerrainType);
            }
        }

        // Generate noise ONLY for terrain types actually used in this chunk
        Dictionary<byte, float[]> terrainDensities = new Dictionary<byte, float[]>();

        foreach (byte terrainID in usedTerrainIDs)
        {
            // Check if we have a noise generator for this terrain
            if (!noiseGenerators.ContainsKey(terrainID))
            {
                UnityEngine.Debug.LogWarning($"Terrain ID {terrainID} used in chunk but no noise generator configured!");
                continue;
            }

            // Get terrain-specific settings
            TerrainTypeDefinitionSO terrainSO = terrainTypeSOs[terrainID];
            float terrainFrequency = relativeFrequency * terrainSO.frequencyMultiplier;
            int3 terrainWorldPos = worldPos;
            terrainWorldPos.y += terrainSO.yOffset / sampleRes;  // Apply Y offset

            FastNoise noise = noiseGenerators[terrainID];
            float[] density = new float[voxelCount];

            noise.GenUniformGrid3D(
                density,
                terrainWorldPos.x,
                terrainWorldPos.y,  // Uses offset Y position
                terrainWorldPos.z,
                side,
                side,
                side,
                terrainFrequency,   // Uses terrain-specific frequency
                seed
            );

            terrainDensities[terrainID] = density;
        }

        // Create final blended density
        float[] finalDensity = new float[voxelCount];

        BlendTerrainNoises(
            finalDensity,
            terrainDensities,
            chunkSize,
            sampleRes,
            side,
            biome
        );

        return finalDensity;
    }

    /// <summary>
    /// Blends between terrain-specific noise fields based on biome hints
    /// </summary>
    static void BlendTerrainNoises(
        float[] finalDensity,
        Dictionary<byte, float[]> terrainDensities,
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
                byte primaryTerrainID = hint.primaryTerrainType;
                byte secondaryTerrainID = hint.secondaryTerrainType;
                float terrainBlendFactor = hint.terrainBlend * 0.00392156862745098f;

                int columnBaseIdx = x + z * side * side;

                // Check if terrains have noise (with error handling)
                bool hasPrimaryNoise = terrainDensities.ContainsKey(primaryTerrainID);
                bool hasSecondaryNoise = terrainDensities.ContainsKey(secondaryTerrainID);

                if (!hasPrimaryNoise)
                {
                    UnityEngine.Debug.LogError($"Primary terrain ID {primaryTerrainID} not found in noise generators! Available: {string.Join(", ", terrainDensities.Keys)}");
                }

                for (int y = 0; y < side; y++)
                {
                    int idx = columnBaseIdx + y * side;

                    // Get primary noise value (fallback to 0 if missing)
                    float primaryValue = hasPrimaryNoise ? terrainDensities[primaryTerrainID][idx] : 0f;

                    // Blend with secondary if needed
                    float finalValue = primaryValue;
                    if (terrainBlendFactor > 0.001f && hasSecondaryNoise)
                    {
                        float secondaryValue = terrainDensities[secondaryTerrainID][idx];
                        finalValue = primaryValue + (secondaryValue - primaryValue) * terrainBlendFactor;
                    }

                    finalDensity[idx] = finalValue;
                }
            }
        }
    }
}
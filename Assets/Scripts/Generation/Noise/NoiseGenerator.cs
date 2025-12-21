//using FastNoise2;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Profiling;

public class NoiseGenerator
{
    private FastNoise noise;
    private int seed;

    public NoiseGenerator(int inSeed)
    {
        seed = inSeed;
        // delve style
        //noise = FastNoise.FromEncodedNodeTree("EAD2KNw/JQAAAIA/PQrXPwAAgD8AAIA/DQAEAAAAuB4FQAcAAAAAAD8AAAAAAAAK1yM+");
        // delve with caps
        //noise = FastNoise.FromEncodedNodeTree("GgABHQAEAAAAAABI4bo/AAAAAAAAAAAAAAAAzcyMPwAAAAAAAAAAAAAAAAABGgABJQAAAIA/AADAPwAAgD8AAIA/DQAEAAAAH4XrPwcAAAAAAD8AAAAAAAEQAArXA0AeAAQAAAAAAAAA8EEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAK1yM9");
        // Hills + Cubes
        //noise = FastNoise.FromEncodedNodeTree("HgAEAAAAAAB7FO4/AAAAAAAAAAAAAAAAAAAAvwAAAAAAAAAAARsAEQACAAAAAAAgQBAAAAAAQBkAEwDD9Sg/DQAEAAAAAAAgQAkAAGZmJj8AAAAAPwEEAAAAAAAAAEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAM3MTD4AMzMzPwAfhWtAARoAARMAKVwvQAwABAAAAK5HYT7//wMAAEjhej8A7FG4vg==");
        //noise = new FastNoise("FractalFBm");
        // Nice Hills
        noise = FastNoise.FromEncodedNodeTree("EwDNzMw+EQACAAAAAAAAQBAAexSuPhkAGQATAB+F6z8lAAAAgD/D9eg/AACAPwAAgD8RAAQAAACuRwFAEAAK12NADQADAAAAAAAAQBAAAAAAPwcAAJqZmT4AKVwPPwDXo3A/AM3MzD0AzcyMPwAUrgdAAQAAj8J1PQEEAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHsULj8AMzMzPwAAAAA/");
        //noise = FastNoise.FromEncodedNodeTree("BAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==");

    }

    /// <summary>
    /// Generates a density field for a chunk at a given LOD, then applies
    /// terrain type and biome-driven shaping.
    /// 
    /// Pipeline:
    /// 1) Generate base 3D noise in one bulk call (FastNoise)
    /// 2) Apply terrain type density curves (affects shape)
    /// 3) Biome data applied later in block assignment
    /// </summary>
    public float[] FillDensity(
        int3 chunkCoord,
        int chunkSize,
        float frequency,
        int sampleRes,
        BiomeData biome,
        NativeArray<float> curveData,
        NativeArray<TerrainTypeData> terrainTypes)
    {
        if (noise == null)
            throw new System.Exception("Noise not initialized");

        int scaledSize = chunkSize / sampleRes;
        int side = scaledSize + 1;
        int voxelCount = side * side * side;

        float[] density = new float[voxelCount];

        int3 worldPos = (chunkCoord * chunkSize) / sampleRes;
        float relativeFrequency = frequency / 100f * sampleRes;

        // Generate base terrain noise
        noise.GenUniformGrid3D(
            density,
            worldPos.x,
            worldPos.y,
            worldPos.z,
            side,
            side,
            side,
            relativeFrequency,
            seed
        );

        // Apply terrain type shaping (density curves)
        ApplyTerrainTypeToDensity(
            density,
            chunkCoord,
            chunkSize,
            sampleRes,
            biome,
            curveData,
            terrainTypes
        );

        return density;
    }

    /// <summary>
    /// Applies terrain type density curves to shape the terrain.
    /// This is where mountains, valleys, plains, etc. get their characteristic shapes.
    /// </summary>
    static void ApplyTerrainTypeToDensity(
        float[] density,
        int3 chunkCoord,
        int chunkSize,
        int sampleRes,
        BiomeData biome,
        NativeArray<float> curveData,
        NativeArray<TerrainTypeData> terrainTypes)
    {
        int scaledSize = chunkSize / sampleRes;
        int side = scaledSize + 1;

        int biomeRes = biome.resolution;
        int biomeSide = biomeRes + 1;

        // Calculate world Y range for this chunk
        int chunkWorldY = chunkCoord.y * chunkSize;

        // Iterate through all voxels
        for (int z = 0; z < side; z++)
        {
            float lz = (float)z / scaledSize;
            int bz = (int)math.round(lz * biomeRes);
            bz = math.min(bz, biomeRes);

            for (int x = 0; x < side; x++)
            {
                float lx = (float)x / scaledSize;
                int bx = (int)math.round(lx * biomeRes);
                bx = math.min(bx, biomeRes);

                // Fetch biome hint for this column
                BiomeHint hint = biome.grid[bx + bz * biomeSide];

                // Get terrain types
                byte primaryTerrainID = hint.primaryTerrainType;
                byte secondaryTerrainID = hint.secondaryTerrainType;
                float terrainBlendFactor = hint.terrainBlend / 255f;

                // Apply to all Y in this column
                for (int y = 0; y < side; y++)
                {
                    int idx = x + y * side + z * side * side;
                    float d = density[idx];

                    // Calculate world Y and normalized height
                    int worldY = chunkWorldY + (y * sampleRes);

                    // Normalize Y based on expected terrain range
                    // Adjust these values based on your world scale
                    float minTerrainY = -320f;  // Bottom of world
                    float maxTerrainY = 320f;   // Top of world
                    float normalizedY = (worldY - minTerrainY) / (maxTerrainY - minTerrainY);
                    normalizedY = math.clamp(normalizedY, 0f, 1f);

                    // Sample primary terrain curve
                    float primaryCurveValue = 0f;
                    if (primaryTerrainID < terrainTypes.Length)
                    {
                        primaryCurveValue = SampleTerrainCurve(
                            terrainTypes[primaryTerrainID],
                            normalizedY,
                            curveData
                        );
                    }

                    // Sample secondary terrain curve (for blending)
                    float secondaryCurveValue = 0f;
                    if (terrainBlendFactor > 0f && secondaryTerrainID < terrainTypes.Length)
                    {
                        secondaryCurveValue = SampleTerrainCurve(
                            terrainTypes[secondaryTerrainID],
                            normalizedY,
                            curveData
                        );
                    }

                    // Blend between primary and secondary terrain curves
                    float finalCurveValue = math.lerp(primaryCurveValue, secondaryCurveValue, terrainBlendFactor);

                    // Apply curve modification to density
                    d += finalCurveValue;

                    density[idx] = d;
                }
            }
        }
    }

    /// <summary>
    /// Samples a terrain type's density curve at a given height.
    /// Uses linear interpolation between curve samples.
    /// </summary>
    static float SampleTerrainCurve(
        TerrainTypeData terrain,
        float normalizedHeight,  // 0-1
        NativeArray<float> curveData)
    {
        // Clamp height to valid range
        normalizedHeight = math.clamp(normalizedHeight, 0f, 1f);

        // Convert to curve sample index
        float floatIndex = normalizedHeight * (terrain.curveResolution - 1);
        int index0 = (int)math.floor(floatIndex);
        int index1 = math.min(index0 + 1, terrain.curveResolution - 1);
        float t = floatIndex - index0;

        // Get curve samples
        int curveIdx0 = terrain.curveStartIndex + index0;
        int curveIdx1 = terrain.curveStartIndex + index1;

        // Bounds check
        if (curveIdx0 >= curveData.Length || curveIdx1 >= curveData.Length)
            return 0f;

        float value0 = curveData[curveIdx0];
        float value1 = curveData[curveIdx1];

        // Linear interpolation
        return math.lerp(value0, value1, t);
    }
}

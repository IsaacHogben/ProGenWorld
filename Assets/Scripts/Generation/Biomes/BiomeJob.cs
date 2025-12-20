using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public struct BiomeJob : IJobParallelFor
{
    public int3 chunkCoord;
    public int chunkSize;
    public int resolution;
    public uint seed;
    public float frequency;
    public int waterLevel;

    [ReadOnly] public NativeArray<TerrainTypeData> terrainTypes;
    [ReadOnly] public NativeArray<byte> terrainAllowedBiomes;
    [ReadOnly] public NativeArray<BiomeDefinition> biomes;

    [WriteOnly] public NativeArray<BiomeHint> output;

    public float terrainBlendWidth;
    public float biomeBlendWidth;

    public void Execute(int index)
    {
        int side = resolution + 1;
        int x = index % side;
        int z = index / side;

        float fx = (float)x / resolution;
        float fz = (float)z / resolution;

        float worldX = chunkCoord.x * chunkSize + fx * chunkSize;
        float worldZ = chunkCoord.z * chunkSize + fz * chunkSize;

        // Single noise for terrain selection (no climate)
        float noiseValue = FractalNoise2D(worldX, worldZ, frequency, seed, 3);

        // Separate climate noise
        float humidity = FractalNoise2D(worldX, worldZ, frequency * 0.8f, seed + 1000, 3);
        float temperature = FractalNoise2D(worldX, worldZ, frequency * 0.7f, seed + 5000, 3);

        // Select terrain types based on noise only (no climate)
        byte primaryTerrain = 0;
        byte secondaryTerrain = 0;
        byte terrainBlend = 0;

        if (terrainTypes.Length > 0)
        {
            SelectTerrainTypes(
                noiseValue,
                out primaryTerrain,
                out secondaryTerrain,
                out terrainBlend
            );
        }

        // Select biomes from primary terrain's allowed list based on climate
        byte primaryBiome = 0;
        byte secondaryBiome = 0;
        byte biomeBlend = 0;

        if (biomes.Length > 0 && primaryTerrain < terrainTypes.Length)
        {
            SelectBiomesFromTerrain(
                primaryTerrain,
                humidity,
                temperature,
                out primaryBiome,
                out secondaryBiome,
                out biomeBlend
            );
        }

        output[index] = new BiomeHint
        {
            primaryTerrainType = primaryTerrain,
            secondaryTerrainType = secondaryTerrain,
            terrainBlend = terrainBlend,
            primaryBiome = primaryBiome,
            secondaryBiome = secondaryBiome,
            biomeBlend = biomeBlend
        };
    }

    // ========================================================================
    // TERRAIN TYPE SELECTION - Based on noise only, evenly distributed
    // ========================================================================

    void SelectTerrainTypes(
        float noiseValue,
        out byte primary,
        out byte secondary,
        out byte blend)
    {
        if (terrainTypes.Length == 0)
        {
            primary = 0;
            secondary = 0;
            blend = 0;
            return;
        }

        if (terrainTypes.Length == 1)
        {
            primary = 0;
            secondary = 0;
            blend = 0;
            return;
        }

        // Use golden ratio for even distribution in 1D noise space
        float goldenRatio = 1.618033988749895f;

        float closestDist = 999f;
        float secondClosestDist = 999f;
        int closestIndex = 0;
        int secondClosestIndex = 1;

        for (int i = 0; i < terrainTypes.Length; i++)
        {
            TerrainTypeData terrain = terrainTypes[i];

            if (terrain.rarityWeight <= 0f)
                continue;

            // Generate evenly-spaced point for this terrain type
            float terrainPoint = (i * goldenRatio) % 1.0f;

            // Distance in 1D noise space (wrapped)
            float dist = math.abs(noiseValue - terrainPoint);
            dist = math.min(dist, 1.0f - dist); // Wrap around

            // Apply rarity as distance modifier
            dist /= math.max(0.1f, terrain.rarityWeight);

            if (dist < closestDist)
            {
                secondClosestDist = closestDist;
                secondClosestIndex = closestIndex;
                closestDist = dist;
                closestIndex = i;
            }
            else if (dist < secondClosestDist)
            {
                secondClosestDist = dist;
                secondClosestIndex = i;
            }
        }

        primary = (byte)closestIndex;
        secondary = (byte)secondClosestIndex;

        // Calculate blend
        float totalDist = closestDist + secondClosestDist;
        if (totalDist < 0.001f)
        {
            blend = 0;
        }
        else
        {
            float rawBlend = closestDist / totalDist;
            float midpoint = 0.5f;
            float scaledBlend = (rawBlend - midpoint) / terrainBlendWidth + midpoint;
            scaledBlend = math.clamp(scaledBlend, 0f, 1f);
            blend = (byte)(scaledBlend * 255f);
        }
    }

    // ========================================================================
    // BIOME SELECTION - From allowed list, scored by climate
    // ========================================================================

    void SelectBiomesFromTerrain(
        byte terrainTypeID,
        float humidity,
        float temperature,
        out byte primary,
        out byte secondary,
        out byte blend)
    {
        TerrainTypeData terrain = terrainTypes[terrainTypeID];

        // No allowed biomes
        if (terrain.allowedBiomesCount == 0)
        {
            primary = 0;
            secondary = 0;
            blend = 0;
            return;
        }

        // Only one allowed biome
        if (terrain.allowedBiomesCount == 1)
        {
            int biomeIndex = terrain.allowedBiomesStartIndex;
            primary = terrainAllowedBiomes[biomeIndex];
            secondary = primary;
            blend = 0;
            return;
        }

        // Score all allowed biomes by climate match
        float bestScore = -999f;
        float secondBestScore = -999f;
        byte bestBiome = 0;
        byte secondBestBiome = 0;

        int allowedEnd = terrain.allowedBiomesStartIndex + terrain.allowedBiomesCount;

        for (int i = terrain.allowedBiomesStartIndex; i < allowedEnd; i++)
        {
            byte biomeID = terrainAllowedBiomes[i];

            // Bounds check
            if (biomeID >= biomes.Length)
                continue;

            BiomeDefinition biome = biomes[biomeID];

            // Score based on climate match
            float score = ScoreBiomeClimate(biome, humidity, temperature);

            if (score > bestScore)
            {
                secondBestScore = bestScore;
                secondBestBiome = bestBiome;
                bestScore = score;
                bestBiome = biomeID;
            }
            else if (score > secondBestScore)
            {
                secondBestScore = score;
                secondBestBiome = biomeID;
            }
        }

        primary = bestBiome;
        secondary = secondBestBiome;

        // Calculate blend based on score difference
        float scoreDiff = bestScore - secondBestScore;

        if (scoreDiff > biomeBlendWidth * 2f)
        {
            blend = 0; // Pure primary
        }
        else
        {
            float t = 1f - (scoreDiff / (biomeBlendWidth * 2f));
            blend = (byte)(math.clamp(t, 0f, 1f) * 255f);
        }
    }

    float ScoreBiomeClimate(BiomeDefinition biome, float humidity, float temperature)
    {
        float score = 0f;

        // Humidity match
        if (humidity >= biome.humidityMin && humidity <= biome.humidityMax)
        {
            // Inside preferred range - score based on how centered we are
            float center = (biome.humidityMin + biome.humidityMax) * 0.5f;
            float range = biome.humidityMax - biome.humidityMin;
            float distFromCenter = math.abs(humidity - center);
            score += (1f - distFromCenter / (range * 0.5f)) * 5f;
        }
        else
        {
            // Outside range - penalize based on distance
            float distToRange = math.min(
                math.abs(humidity - biome.humidityMin),
                math.abs(humidity - biome.humidityMax)
            );
            score -= distToRange * 10f;
        }

        // Temperature match
        if (temperature >= biome.temperatureMin && temperature <= biome.temperatureMax)
        {
            float center = (biome.temperatureMin + biome.temperatureMax) * 0.5f;
            float range = biome.temperatureMax - biome.temperatureMin;
            float distFromCenter = math.abs(temperature - center);
            score += (1f - distFromCenter / (range * 0.5f)) * 5f;
        }
        else
        {
            float distToRange = math.min(
                math.abs(temperature - biome.temperatureMin),
                math.abs(temperature - biome.temperatureMax)
            );
            score -= distToRange * 10f;
        }

        return score;
    }

    // ========================================================================
    // NOISE FUNCTIONS
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352d;
        x ^= x >> 15;
        x *= 0x846ca68b;
        x ^= x >> 16;
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Hash01(int x, int z, uint seed)
    {
        uint h =
            (uint)x * 374761393u ^
            (uint)z * 668265263u ^
            seed * 1442695041u;
        return (Hash(h) & 0x00FFFFFF) * (1f / 16777216f);
    }

    static float ValueNoise2D(float x, float z, float freq, uint seed)
    {
        x *= freq;
        z *= freq;

        int xi = (int)math.floor(x);
        int zi = (int)math.floor(z);
        float xf = x - xi;
        float zf = z - zi;

        float v00 = Hash01(xi, zi, seed);
        float v10 = Hash01(xi + 1, zi, seed);
        float v01 = Hash01(xi, zi + 1, seed);
        float v11 = Hash01(xi + 1, zi + 1, seed);

        float u = xf * xf * (3f - 2f * xf);
        float v = zf * zf * (3f - 2f * zf);

        return math.lerp(
            math.lerp(v00, v10, u),
            math.lerp(v01, v11, u),
            v
        );
    }

    static float FractalNoise2D(float x, float z, float freq, uint seed, int octaves)
    {
        float sum = 0f;
        float amplitude = 1f;
        float amplitudeSum = 0f;

        for (int i = 0; i < octaves; i++)
        {
            sum += ValueNoise2D(x, z, freq, seed + (uint)i * 100) * amplitude;
            amplitudeSum += amplitude;

            freq *= 2f;
            amplitude *= 0.5f;
        }

        return sum / amplitudeSum;
    }
}
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

// Original working logic + FastNoise optimization + Climate Grid support
[BurstCompile]
public struct BiomeJob : IJobParallelFor
{
    public int3 chunkCoord;
    public int chunkSize;
    public int resolution;
    public int waterLevel;

    [ReadOnly] public NativeArray<TerrainTypeData> terrainTypes;
    [ReadOnly] public NativeArray<byte> terrainAllowedBiomes;
    [ReadOnly] public NativeArray<BiomeDefinition> biomes;

    // Climate Grid data
    public int humidityDivisions;
    public int temperatureDivisions;
    public byte defaultBiome;
    [ReadOnly] public NativeArray<ClimateGridCell> climateGrid;
    [ReadOnly] public NativeArray<byte> climateCellBiomes; // All biomes from all cells, flattened
    [ReadOnly] public NativeArray<float> biomeRarityWeights; // Indexed by BiomeType
    [ReadOnly] public NativeArray<int> biomePriority; // Indexed by BiomeType, value = array position (lower = higher priority)

    // Pre-computed noise grids (using FastNoise)
    [ReadOnly] public NativeArray<float> terrainNoiseGrid;
    [ReadOnly] public NativeArray<float> humidityNoiseGrid;
    [ReadOnly] public NativeArray<float> temperatureNoiseGrid;

    [WriteOnly] public NativeArray<BiomeHint> output;

    public float terrainBlendWidth;
    public float biomeBlendWidth;
    public uint randomSeed;

    public void Execute(int index)
    {
        // Read pre-computed noise values
        float noiseValue = terrainNoiseGrid[index];
        float humidity = humidityNoiseGrid[index];
        float temperature = temperatureNoiseGrid[index];

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

        // Select biomes using climate grid + terrain allowed list
        byte primaryBiome = 0;
        byte secondaryBiome = 0;
        byte biomeBlend = 0;

        if (climateGrid.Length > 0 && primaryTerrain < terrainTypes.Length)
        {
            SelectBiomesFromClimateGrid(
                primaryTerrain,
                humidity,
                temperature,
                index,
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
    // TERRAIN TYPE SELECTION - Original working version with even distribution
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

        // Calculate total weight
        float totalWeight = 0f;
        for (int i = 0; i < terrainTypes.Length; i++)
        {
            totalWeight += math.max(0.1f, terrainTypes[i].rarityWeight);
        }

        // Build weighted ranges and find closest + second closest
        float closestDist = 999f;
        float secondClosestDist = 999f;
        int closestIndex = 0;
        int secondClosestIndex = 1;

        float cumulative = 0f;

        for (int i = 0; i < terrainTypes.Length; i++)
        {
            TerrainTypeData terrain = terrainTypes[i];
            float weight = math.max(0.1f, terrain.rarityWeight);
            float normalizedWeight = weight / totalWeight;

            // This terrain's center point in [0,1] space
            float terrainCenter = cumulative + (normalizedWeight * 0.5f);

            // Distance from noise to this terrain's center (with wrapping)
            float dist = math.abs(noiseValue - terrainCenter);
            dist = math.min(dist, 1.0f - dist); // Wrap around

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

            cumulative += normalizedWeight;
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
    // BIOME SELECTION - Climate Grid + Terrain Filtering + Weighted Random
    // ========================================================================

    void SelectBiomesFromClimateGrid(
        byte terrainTypeID,
        float humidity,
        float temperature,
        int randomIndex,
        out byte primary,
        out byte secondary,
        out byte blend)
    {
        // Get climate cell
        int humIndex = (int)(humidity * humidityDivisions);
        int tempIndex = (int)(temperature * temperatureDivisions);
        humIndex = math.clamp(humIndex, 0, humidityDivisions - 1);
        tempIndex = math.clamp(tempIndex, 0, temperatureDivisions - 1);

        // Try current cell first
        if (TrySelectFromCell(terrainTypeID, humIndex, tempIndex, randomIndex, out primary, out secondary, out blend))
            return;

        // No valid biomes in current cell - search neighbors in spiral pattern
        int maxSearchRadius = math.max(humidityDivisions, temperatureDivisions);

        for (int radius = 1; radius <= maxSearchRadius; radius++)
        {
            // Check cells at this radius
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    // Skip if not on the edge of this radius
                    if (math.abs(dx) != radius && math.abs(dy) != radius)
                        continue;

                    int checkHumIndex = humIndex + dx;
                    int checkTempIndex = tempIndex + dy;

                    // Skip out of bounds
                    if (checkHumIndex < 0 || checkHumIndex >= humidityDivisions ||
                        checkTempIndex < 0 || checkTempIndex >= temperatureDivisions)
                        continue;

                    if (TrySelectFromCell(terrainTypeID, checkHumIndex, checkTempIndex, randomIndex, out primary, out secondary, out blend))
                        return;
                }
            }
        }

        // Fallback: try to find ANY allowed biome from terrain (ignore climate)
        TerrainTypeData terrain = terrainTypes[terrainTypeID];
        if (terrain.allowedBiomesCount > 0)
        {
            // Just pick the first allowed biome
            primary = terrainAllowedBiomes[terrain.allowedBiomesStartIndex];
            secondary = primary;
            blend = 0;
            return;
        }

        // Ultimate fallback
        primary = defaultBiome;
        secondary = defaultBiome;
        blend = 0;
    }

    bool TrySelectFromCell(
        byte terrainTypeID,
        int humIndex,
        int tempIndex,
        int randomIndex,
        out byte primary,
        out byte secondary,
        out byte blend)
    {
        primary = 0;
        secondary = 0;
        blend = 0;

        int cellIndex = tempIndex * humidityDivisions + humIndex;
        ClimateGridCell cell = climateGrid[cellIndex];

        // Get terrain's allowed biomes
        TerrainTypeData terrain = terrainTypes[terrainTypeID];

        // No biomes in this cell
        if (cell.biomeCount == 0)
            return false;

        // Build list of valid biomes (in cell AND allowed by terrain)
        const int maxValid = 32;
        NativeArray<byte> validBiomes = new NativeArray<byte>(maxValid, Allocator.Temp);
        NativeArray<float> validWeights = new NativeArray<float>(maxValid, Allocator.Temp);
        int validCount = 0;

        // First, gather terrain's allowed biomes into a temp array for faster lookup
        const int maxAllowed = 32;
        NativeArray<byte> terrainAllowed = new NativeArray<byte>(maxAllowed, Allocator.Temp);
        int terrainAllowedCount = math.min(terrain.allowedBiomesCount, maxAllowed);

        for (int i = 0; i < terrainAllowedCount; i++)
        {
            terrainAllowed[i] = terrainAllowedBiomes[terrain.allowedBiomesStartIndex + i];
        }

        // Now check each biome in the climate cell
        for (int i = 0; i < cell.biomeCount && validCount < maxValid; i++)
        {
            byte biomeID = climateCellBiomes[cell.biomeStartIndex + i];

            // Check if terrain allows this biome
            bool allowed = false;
            for (int j = 0; j < terrainAllowedCount; j++)
            {
                if (terrainAllowed[j] == biomeID)
                {
                    allowed = true;
                    break;
                }
            }

            if (allowed)
            {
                validBiomes[validCount] = biomeID;
                // Make sure biomeID is within bounds of rarityWeights array
                if (biomeID < biomeRarityWeights.Length)
                {
                    validWeights[validCount] = biomeRarityWeights[biomeID];
                }
                else
                {
                    validWeights[validCount] = 1.0f; // Default weight
                }
                validCount++;
            }
        }

        terrainAllowed.Dispose();

        // No valid biomes after filtering
        if (validCount == 0)
        {
            validBiomes.Dispose();
            validWeights.Dispose();
            return false;
        }

        // Only one valid biome
        if (validCount == 1)
        {
            primary = validBiomes[0];
            secondary = validBiomes[0];
            blend = 0;
            validBiomes.Dispose();
            validWeights.Dispose();
            return true;
        }

        // Weighted random selection for primary
        float totalWeight = 0f;
        for (int i = 0; i < validCount; i++)
            totalWeight += validWeights[i];

        // Generate pseudo-random value [0,1]
        uint hash = (uint)randomIndex * 374761393u + randomSeed;
        hash ^= hash >> 16;
        hash *= 0x7feb352d;
        hash ^= hash >> 15;
        float random = (hash & 0x00FFFFFF) * (1f / 16777216f);

        // Select primary biome
        float roll = random * totalWeight;
        float cumulative = 0f;
        int primaryIndex = 0;

        for (int i = 0; i < validCount; i++)
        {
            cumulative += validWeights[i];
            if (roll < cumulative)
            {
                primaryIndex = i;
                break;
            }
        }

        primary = validBiomes[primaryIndex];

        // Select secondary - use the biome with lowest priority value (earliest in BiomeDataManager array)
        int lowestPriority = int.MaxValue;
        int secondaryIndex = primaryIndex;

        for (int i = 0; i < validCount; i++)
        {
            byte biomeID = validBiomes[i];
            int priority = biomeID < biomePriority.Length ? biomePriority[biomeID] : int.MaxValue;

            if (i != primaryIndex && priority < lowestPriority)
            {
                lowestPriority = priority;
                secondaryIndex = i;
            }
        }

        secondary = validBiomes[secondaryIndex];

        // No real blending - just use primary (blend = 0)
        blend = 0;

        validBiomes.Dispose();
        validWeights.Dispose();
        return true;
    }
}
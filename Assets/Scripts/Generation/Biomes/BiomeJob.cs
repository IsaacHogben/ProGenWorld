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
    [ReadOnly] public NativeArray<byte> climateCellBiomes;
    [ReadOnly] public NativeArray<float> biomeRarityWeights;
    [ReadOnly] public NativeArray<int> biomePriority;

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
        float noiseValue = terrainNoiseGrid[index];
        float humidity = humidityNoiseGrid[index];
        float temperature = temperatureNoiseGrid[index];

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

        byte primaryBiome = 0;
        byte secondaryBiome = 0;
        byte biomeBlend = 0;

        if (climateGrid.Length > 0 && primaryTerrain < terrainTypes.Length)
        {
            SelectBiomesFromClimateGrid(
                primaryTerrain,
                secondaryTerrain,
                terrainBlend,
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
    // TERRAIN TYPE SELECTION
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

        float totalWeight = 0f;
        for (int i = 0; i < terrainTypes.Length; i++)
            totalWeight += math.max(0.1f, terrainTypes[i].rarityWeight);

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

            float terrainCenter = cumulative + (normalizedWeight * 0.5f);

            float dist = math.abs(noiseValue - terrainCenter);
            dist = math.min(dist, 1.0f - dist);

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
    // BIOME SELECTION
    // ========================================================================

    void SelectBiomesFromClimateGrid(
        byte primaryTerrainID,
        byte secondaryTerrainID,
        byte terrainBlend,
        float humidity,
        float temperature,
        int randomIndex,
        out byte primary,
        out byte secondary,
        out byte blend)
    {
        int humIndex = (int)(humidity * humidityDivisions);
        int tempIndex = (int)(temperature * temperatureDivisions);
        humIndex = math.clamp(humIndex, 0, humidityDivisions - 1);
        tempIndex = math.clamp(tempIndex, 0, temperatureDivisions - 1);

        // Convert local index to world-space coordinates for stable hashing
        int localX = randomIndex % resolution;
        int localZ = randomIndex / resolution;
        int worldX = chunkCoord.x * chunkSize + localX;
        int worldZ = chunkCoord.z * chunkSize + localZ;

        // Look up biome for primary terrain type
        byte primaryTerrainBiome = defaultBiome;
        GetBiomeForTerrain(primaryTerrainID, humIndex, tempIndex, randomIndex, humidity, temperature, worldX, worldZ, out primaryTerrainBiome);

        // Look up biome for secondary terrain type
        byte secondaryTerrainBiome = defaultBiome;
        if (secondaryTerrainID != primaryTerrainID && secondaryTerrainID < terrainTypes.Length)
        {
            GetBiomeForTerrain(secondaryTerrainID, humIndex, tempIndex, randomIndex, humidity, temperature, worldX, worldZ, out secondaryTerrainBiome);
        }
        else
        {
            secondaryTerrainBiome = primaryTerrainBiome;
        }

        // If both terrain types resolve to the same biome, no dithering needed
        if (primaryTerrainBiome == secondaryTerrainBiome)
        {
            primary = primaryTerrainBiome;
            secondary = secondaryTerrainBiome;
            blend = 0;
            return;
        }

        // Dither between the two biomes using terrainBlend as the chance
        // terrainBlend is 0 = fully primary terrain, 255 = fully secondary terrain
        // Remap blend into a sharper transition using biomeBlendWidth
        // biomeBlendWidth < 1.0 = narrower dither zone, 1.0 = same as terrain blend
        float rawBlend = terrainBlend / 255f;
        float blendChance = math.saturate((rawBlend - (1f - biomeBlendWidth)) / biomeBlendWidth);

        uint ditherHash = (uint)(worldX * 2747636419u ^ worldZ * 374761393u) + randomSeed ^ 0xDEADBEEF;
        ditherHash ^= ditherHash >> 16;
        ditherHash *= 0x45d9f3b;
        ditherHash ^= ditherHash >> 15;
        float ditherRoll = (ditherHash & 0x00FFFFFF) * (1f / 16777216f);

        if (blendChance > ditherRoll)
        {
            // This point dithers to secondary terrain's biome
            primary = secondaryTerrainBiome;
            secondary = primaryTerrainBiome;
        }
        else
        {
            primary = primaryTerrainBiome;
            secondary = secondaryTerrainBiome;
        }

        blend = 0;
    }

    void GetBiomeForTerrain(
        byte terrainTypeID,
        int humIndex,
        int tempIndex,
        int randomIndex,
        float humidity,
        float temperature,
        int worldX,
        int worldZ,
        out byte biome)
    {
        byte secondary;
        byte blend;

        if (TrySelectFromCell(terrainTypeID, humIndex, tempIndex, randomIndex, worldX, worldZ, out biome, out secondary, out blend))
            return;

        int maxSearchRadius = math.max(humidityDivisions, temperatureDivisions);

        for (int radius = 1; radius <= maxSearchRadius; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (math.abs(dx) != radius && math.abs(dy) != radius)
                        continue;

                    int checkHumIndex = humIndex + dx;
                    int checkTempIndex = tempIndex + dy;

                    if (checkHumIndex < 0 || checkHumIndex >= humidityDivisions ||
                        checkTempIndex < 0 || checkTempIndex >= temperatureDivisions)
                        continue;

                    if (TrySelectFromCell(terrainTypeID, checkHumIndex, checkTempIndex, randomIndex, worldX, worldZ, out biome, out secondary, out blend))
                        return;
                }
            }
        }

        // Fallback: use first allowed biome for terrain, ignoring climate
        TerrainTypeData terrain = terrainTypes[terrainTypeID];
        if (terrain.allowedBiomesCount > 0)
        {
            biome = terrainAllowedBiomes[terrain.allowedBiomesStartIndex];
            return;
        }

        // Ultimate fallback
        biome = defaultBiome;
    }

    bool TrySelectFromCell(
        byte terrainTypeID,
        int humIndex,
        int tempIndex,
        int randomIndex,
        int worldX,
        int worldZ,
        out byte primary,
        out byte secondary,
        out byte blend)
    {
        primary = 0;
        secondary = 0;
        blend = 0;

        int cellIndex = tempIndex * humidityDivisions + humIndex;
        ClimateGridCell cell = climateGrid[cellIndex];

        TerrainTypeData terrain = terrainTypes[terrainTypeID];

        if (cell.biomeCount == 0)
            return false;

        // Build valid biome list (intersection of cell biomes and terrain allowed biomes)
        const int maxValid = 32;
        NativeArray<byte> validBiomes = new NativeArray<byte>(maxValid, Allocator.Temp);
        NativeArray<float> validWeights = new NativeArray<float>(maxValid, Allocator.Temp);
        int validCount = 0;

        const int maxAllowed = 32;
        NativeArray<byte> terrainAllowed = new NativeArray<byte>(maxAllowed, Allocator.Temp);
        int terrainAllowedCount = math.min(terrain.allowedBiomesCount, maxAllowed);

        for (int i = 0; i < terrainAllowedCount; i++)
            terrainAllowed[i] = terrainAllowedBiomes[terrain.allowedBiomesStartIndex + i];

        for (int i = 0; i < cell.biomeCount && validCount < maxValid; i++)
        {
            byte biomeID = climateCellBiomes[cell.biomeStartIndex + i];

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
                validWeights[validCount] = biomeID < biomeRarityWeights.Length
                    ? biomeRarityWeights[biomeID]
                    : 1.0f;
                validCount++;
            }
        }

        terrainAllowed.Dispose();

        if (validCount == 0)
        {
            validBiomes.Dispose();
            validWeights.Dispose();
            return false;
        }

        if (validCount == 1)
        {
            primary = validBiomes[0];
            secondary = validBiomes[0];
            blend = 0;
            validBiomes.Dispose();
            validWeights.Dispose();
            return true;
        }

        // World-stable hash for biome selection
        uint selectionHash = (uint)(worldX * 1836311903u ^ worldZ * 2971215073u) + randomSeed;
        selectionHash ^= selectionHash >> 16;
        selectionHash *= 0x7feb352d;
        selectionHash ^= selectionHash >> 15;
        float random = (selectionHash & 0x00FFFFFF) * (1f / 16777216f);

        // Weighted random selection for primary
        float totalWeight = 0f;
        for (int i = 0; i < validCount; i++)
            totalWeight += validWeights[i];

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

        // Select secondary by lowest priority value (excluding primary)
        int lowestPriority = int.MaxValue;
        int secondaryIndex = primaryIndex;

        for (int i = 0; i < validCount; i++)
        {
            if (i == primaryIndex) continue;
            byte biomeID = validBiomes[i];
            int priority = biomeID < biomePriority.Length ? biomePriority[biomeID] : int.MaxValue;

            if (priority < lowestPriority)
            {
                lowestPriority = priority;
                secondaryIndex = i;
            }
        }

        secondary = validBiomes[secondaryIndex];
        blend = 0;

        validBiomes.Dispose();
        validWeights.Dispose();
        return true;
    }
}
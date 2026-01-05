using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

/// <summary>
/// Converts ScriptableObject definitions to Burst-compatible NativeArrays
/// Auto-assigns terrain IDs and validates biome references
/// </summary>
public class BiomeDataManager : MonoBehaviour
{
    [Header("Definitions")]
    [Tooltip("All terrain type definitions (order determines auto-assigned IDs)")]
    public TerrainTypeDefinitionSO[] terrainTypes;

    [Tooltip("All biome definitions")]
    public BiomeDefinitionSO[] biomes;

    [Header("Climate System")]
    public ClimateGridSO climateGrid;

    [Header("Settings")]
    [Tooltip("Water level Y coordinate")]
    public int waterLevel = -90;

    private NativeArray<ClimateGridCell> climateGridCells;
    private NativeArray<byte> climateCellBiomes;
    private NativeArray<float> biomeRarityWeights;

    // Native data for jobs
    private NativeArray<BiomeDefinition> biomeDefinitions;
    private NativeArray<TerrainTypeData> terrainTypeData;
    private NativeArray<float> allCurveData;
    private NativeArray<BiomeData> biomeData;
    private NativeArray<int> biomePriority;
    private NativeArray<DecorationData> decorationData;
    private NativeArray<byte> decorationSpawnBlocks;
    private NativeArray<byte> terrainAllowedBiomes;

    private bool isInitialized = false;

    void Awake()
    {
        Initialize();
    }

    public void Initialize()
    {
        if (isInitialized)
        {
            Debug.LogWarning("BiomeDataManager already initialized!");
            return;
        }

        if (terrainTypes == null || terrainTypes.Length == 0)
        {
            Debug.LogError("No terrain types assigned!");
            return;
        }

        if (biomes == null || biomes.Length == 0)
        {
            Debug.LogError("No biomes assigned!");
            return;
        }

        // Auto-assign terrain IDs based on array order
        AssignTerrainIDs();

        // Validate biome references
        ValidateBiomeReferences();

        ConvertTerrainTypes();
        ConvertBiomes();
        ConvertClimateGrid();
        isInitialized = true;
        Debug.Log($"<color=green>BiomeDataManager initialized!</color> {terrainTypes.Length} terrain types, {biomes.Length} biomes");
    }

    private void AssignTerrainIDs()
    {
        for (int i = 0; i < terrainTypes.Length; i++)
        {
            terrainTypes[i].SetTerrainID((byte)i);
            Debug.Log($"Terrain '{terrainTypes[i].terrainTypeName}' assigned ID: {i}");
        }
    }

    private void ValidateBiomeReferences()
    {
        // Build set of available biome types
        HashSet<BiomeType> availableBiomes = new HashSet<BiomeType>();
        foreach (var biome in biomes)
        {
            if (biome != null)
                availableBiomes.Add(biome.biomeType);
        }

        // Check each terrain's allowed biomes
        foreach (var terrain in terrainTypes)
        {
            if (terrain == null) continue;

            foreach (var biomeType in terrain.allowedBiomes)
            {
                if (!availableBiomes.Contains(biomeType))
                {
                    Debug.LogWarning($"Terrain '{terrain.terrainTypeName}' references biome '{biomeType}' " +
                                   $"but no BiomeDefinitionSO exists for it!");
                }
            }

            if (terrain.allowedBiomes.Count == 0)
            {
                Debug.LogWarning($"Terrain '{terrain.terrainTypeName}' has no allowed biomes!");
            }
        }
    }

    private void ConvertTerrainTypes()
    {
        // Count total allowed biomes across all terrains
        int totalAllowedBiomes = 0;
        foreach (var terrainType in terrainTypes)
        {
            totalAllowedBiomes += terrainType.allowedBiomes.Count;
        }

        // Calculate total curve samples
        int totalCurveSamples = 0;
        foreach (var terrainType in terrainTypes)
        {
            totalCurveSamples += terrainType.curveResolution;
        }

        // Allocate
        terrainTypeData = new NativeArray<TerrainTypeData>(terrainTypes.Length, Allocator.Persistent);
        allCurveData = new NativeArray<float>(totalCurveSamples, Allocator.Persistent);
        terrainAllowedBiomes = new NativeArray<byte>(totalAllowedBiomes, Allocator.Persistent);

        int curveDataIndex = 0;
        int allowedBiomesIndex = 0;

        for (int i = 0; i < terrainTypes.Length; i++)
        {
            var terrainType = terrainTypes[i];

            // Bake curve
            float[] bakedCurve = terrainType.BakeCurve();
            for (int j = 0; j < bakedCurve.Length; j++)
            {
                allCurveData[curveDataIndex + j] = bakedCurve[j];
            }

            // Copy allowed biomes (convert from BiomeType enum to byte)
            int allowedBiomesStart = allowedBiomesIndex;
            for (int j = 0; j < terrainType.allowedBiomes.Count; j++)
            {
                terrainAllowedBiomes[allowedBiomesIndex++] = (byte)terrainType.allowedBiomes[j];
            }

            // Convert to struct
            terrainTypeData[i] = terrainType.ToTerrainTypeData(curveDataIndex, allowedBiomesStart);

            curveDataIndex += terrainType.curveResolution;
        }
    }

    private void ConvertBiomes()
    {
        // Find max biome enum value to size arrays correctly
        int maxBiomeID = 0;
        foreach (var biome in biomes)
        {
            maxBiomeID = Mathf.Max(maxBiomeID, (int)biome.biomeType);
        }

        // Count total decorations and spawn blocks
        int totalDecorations = 0;
        int totalSpawnBlocks = 0;

        foreach (var biome in biomes)
        {
            totalDecorations += biome.trees.Length;
            totalDecorations += biome.vegetation.Length;
            totalDecorations += biome.rocks.Length;
            totalDecorations += biome.aliens.Length;

            foreach (var tree in biome.trees)
                totalSpawnBlocks += tree.spawnOnBlocks.Length;
            foreach (var veg in biome.vegetation)
                totalSpawnBlocks += veg.spawnOnBlocks.Length;
            foreach (var rock in biome.rocks)
                totalSpawnBlocks += rock.spawnOnBlocks.Length;
            foreach (var alien in biome.aliens)
                totalSpawnBlocks += alien.spawnOnBlocks.Length;
        }

        // Allocate arrays sized by max enum value (not biomes.Length!)
        int biomeArraySize = maxBiomeID + 1;
        biomeData = new NativeArray<BiomeData>(biomeArraySize, Allocator.Persistent);
        biomeDefinitions = new NativeArray<BiomeDefinition>(biomeArraySize, Allocator.Persistent);

        // Create priority array - maps BiomeType enum to array position
        biomePriority = new NativeArray<int>(biomeArraySize, Allocator.Persistent);
        for (int i = 0; i < biomeArraySize; i++)
            biomePriority[i] = int.MaxValue; // Default = lowest priority

        decorationData = new NativeArray<DecorationData>(totalDecorations, Allocator.Persistent);
        decorationSpawnBlocks = new NativeArray<byte>(totalSpawnBlocks, Allocator.Persistent);

        // Initialize with default values for unused enum slots
        for (int i = 0; i < biomeArraySize; i++)
        {
            biomeData[i] = new BiomeData
            {
                biomeID = (byte)i,
                surfaceBlock = 0,
                subsurfaceBlock = 0,
                deepBlock = 0,
                sedimentBlock = 0,
                gradientThreshold1 = 0.005f,
                gradientThreshold2 = 0.002f,
                decorationStartIndex = 0,
                decorationCount = 0
            };

            biomeDefinitions[i] = new BiomeDefinition
            {
                biomeID = (byte)i,
                surfaceBlock = 0,
                subsurfaceBlock = 0,
                deepBlock = 0,
                sedimentBlock = 0,
                gradientThreshold1 = 0.005f,
                gradientThreshold2 = 0.002f,
                decorationStartIndex = 0,
                decorationCount = 0,
                humidityMin = 0f,
                humidityMax = 1f,
                temperatureMin = 0f,
                temperatureMax = 1f
            };
        }

        int decorationIndex = 0;
        int spawnBlockIndex = 0;

        // Now fill in each biome at its enum index
        for (int i = 0; i < biomes.Length; i++)
        {
            var biome = biomes[i];
            int biomeIndex = (int)biome.biomeType; // Use enum value as index!
            // Store array position as priority (lower array index = higher priority)
            biomePriority[biomeIndex] = i;
            int decorationStartIndex = decorationIndex;

            // Trees
            foreach (var tree in biome.trees)
            {
                int spawnBlockStart = spawnBlockIndex;
                foreach (var block in tree.spawnOnBlocks)
                    decorationSpawnBlocks[spawnBlockIndex++] = (byte)block;

                decorationData[decorationIndex++] = new DecorationData
                {
                    category = (byte)DecorationCategory.Tree,
                    typeID = (byte)tree.treeType,
                    spawnChance = tree.spawnChance / 10000f, // Convert to 0-1
                    spawnBlockStartIndex = spawnBlockStart,
                    spawnBlockCount = tree.spawnOnBlocks.Length
                };
            }

            // Vegetation
            foreach (var veg in biome.vegetation)
            {
                int spawnBlockStart = spawnBlockIndex;
                foreach (var block in veg.spawnOnBlocks)
                    decorationSpawnBlocks[spawnBlockIndex++] = (byte)block;

                decorationData[decorationIndex++] = new DecorationData
                {
                    category = (byte)DecorationCategory.Vegetation,
                    typeID = (byte)veg.vegetationType,
                    spawnChance = veg.spawnChance / 10000f,
                    spawnBlockStartIndex = spawnBlockStart,
                    spawnBlockCount = veg.spawnOnBlocks.Length
                };
            }

            // Rocks
            foreach (var rock in biome.rocks)
            {
                int spawnBlockStart = spawnBlockIndex;
                foreach (var block in rock.spawnOnBlocks)
                    decorationSpawnBlocks[spawnBlockIndex++] = (byte)block;

                decorationData[decorationIndex++] = new DecorationData
                {
                    category = (byte)DecorationCategory.Rock,
                    typeID = (byte)rock.rockType,
                    spawnChance = rock.spawnChance / 10000f,
                    spawnBlockStartIndex = spawnBlockStart,
                    spawnBlockCount = rock.spawnOnBlocks.Length
                };
            }

            // Aliens
            foreach (var alien in biome.aliens)
            {
                int spawnBlockStart = spawnBlockIndex;
                foreach (var block in alien.spawnOnBlocks)
                    decorationSpawnBlocks[spawnBlockIndex++] = (byte)block;

                decorationData[decorationIndex++] = new DecorationData
                {
                    category = (byte)DecorationCategory.Alien,
                    typeID = (byte)alien.alienType,
                    spawnChance = alien.spawnChance / 10000f,
                    spawnBlockStartIndex = spawnBlockStart,
                    spawnBlockCount = alien.spawnOnBlocks.Length
                };
            }

            int decorationCount = decorationIndex - decorationStartIndex;
            biomeDefinitions[biomeIndex] = biome.ToBiomeDefinition(decorationStartIndex, decorationCount);

            // Create job-safe definition at the enum index
            biomeData[biomeIndex] = new BiomeData
            {
                biomeID = biome.BiomeID,
                surfaceBlock = (byte)biome.surfaceBlock,
                subsurfaceBlock = (byte)biome.subsurfaceBlock,
                deepBlock = (byte)biome.deepBlock,
                sedimentBlock = (byte)biome.sedimentBlock,
                gradientThreshold1 = biome.gradientThreshold1,
                gradientThreshold2 = biome.gradientThreshold2,
                decorationStartIndex = decorationStartIndex,
                decorationCount = decorationCount
            };

            Debug.Log($"Biome {biome.biomeType} (enum={biome.biomeType}, index={biomeIndex}): " +
                      $"H[{biome.humidityRange.x:F2}-{biome.humidityRange.y:F2}] " +
                      $"T[{biome.temperatureRange.x:F2}-{biome.temperatureRange.y:F2}]");
        }

        Debug.Log($"Biome array size: {biomeArraySize} (to fit enum values 0-{maxBiomeID})");
    }
    private void ConvertClimateGrid()
    {
        int cellCount = climateGrid.humidityDivisions * climateGrid.temperatureDivisions;
        climateGridCells = new NativeArray<ClimateGridCell>(cellCount, Allocator.Persistent);

        // Count total biomes
        int totalBiomes = 0;
        foreach (var cell in climateGrid.cells)
            totalBiomes += cell.biomes.Count;

        climateCellBiomes = new NativeArray<byte>(totalBiomes, Allocator.Persistent);

        int biomeIndex = 0;
        for (int i = 0; i < climateGrid.cells.Count; i++)
        {
            var cell = climateGrid.cells[i];
            int startIndex = biomeIndex;

            foreach (var biomeType in cell.biomes)
            {
                climateCellBiomes[biomeIndex++] = (byte)biomeType;
            }

            climateGridCells[i] = new ClimateGridCell
            {
                biomeStartIndex = startIndex,
                biomeCount = cell.biomes.Count
            };
        }

        // Build rarity weights array (indexed by BiomeType)
        int maxBiomeID = 0;
        foreach (var biome in biomes)
            maxBiomeID = Mathf.Max(maxBiomeID, (int)biome.biomeType);

        biomeRarityWeights = new NativeArray<float>(maxBiomeID + 1, Allocator.Persistent);
        for (int i = 0; i < biomeRarityWeights.Length; i++)
            biomeRarityWeights[i] = 1.0f; // default

        foreach (var biome in biomes)
            biomeRarityWeights[(int)biome.biomeType] = biome.rarityWeight;

        Debug.Log($"Climate grid: {cellCount} cells, {totalBiomes} total biome assignments");
    }
    [ContextMenu("Debug Terrain Allowed Biomes")]
    public void DebugTerrainAllowedBiomes()
    {
        if (!isInitialized)
        {
            Debug.LogWarning("Not initialized yet!");
            return;
        }

        Debug.Log("=== TERRAIN ALLOWED BIOMES DEBUG ===");

        for (int i = 0; i < terrainTypes.Length; i++)
        {
            var terrain = terrainTypes[i];
            var data = terrainTypeData[i];

            Debug.Log($"\nTerrain {i}: '{terrain.terrainTypeName}' (ID: {terrain.TerrainID})");
            Debug.Log($"  allowedBiomesStartIndex: {data.allowedBiomesStartIndex}");
            Debug.Log($"  allowedBiomesCount: {data.allowedBiomesCount}");

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("  Allowed Biomes: [");

            for (int j = 0; j < data.allowedBiomesCount; j++)
            {
                int index = data.allowedBiomesStartIndex + j;
                byte biomeID = terrainAllowedBiomes[index];

                // Find the biome name
                string biomeName = ((BiomeType)biomeID).ToString();
                sb.Append($"{biomeName}({biomeID})");

                if (j < data.allowedBiomesCount - 1)
                    sb.Append(", ");
            }
            sb.Append("]");
            Debug.Log(sb.ToString());
        }
    }

    [ContextMenu("Debug Climate Grid")]
    public void DebugClimateGrid()
    {
        if (!isInitialized || climateGrid == null)
        {
            Debug.LogWarning("Not initialized or no climate grid!");
            return;
        }

        Debug.Log("=== CLIMATE GRID DEBUG ===");
        Debug.Log($"Grid size: {climateGrid.humidityDivisions}x{climateGrid.temperatureDivisions}");
        Debug.Log($"Default biome: {climateGrid.defaultBiome}");

        for (int tempIdx = climateGrid.temperatureDivisions - 1; tempIdx >= 0; tempIdx--)
        {
            for (int humIdx = 0; humIdx < climateGrid.humidityDivisions; humIdx++)
            {
                int cellIdx = tempIdx * climateGrid.humidityDivisions + humIdx;
                var cell = climateGridCells[cellIdx];

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append($"Cell [{humIdx},{tempIdx}] ({climateGrid.GetCellLabel(humIdx, tempIdx)}): [");

                for (int i = 0; i < cell.biomeCount; i++)
                {
                    byte biomeID = climateCellBiomes[cell.biomeStartIndex + i];
                    string biomeName = ((BiomeType)biomeID).ToString();
                    sb.Append($"{biomeName}({biomeID})");

                    if (i < cell.biomeCount - 1)
                        sb.Append(", ");
                }
                sb.Append("]");
                Debug.Log(sb.ToString());
            }
        }
    }
    public bool IsInitialized => isInitialized;
    public NativeArray<TerrainTypeData> GetTerrainTypeData() => terrainTypeData;
    public NativeArray<byte> GetTerrainAllowedBiomes() => terrainAllowedBiomes;
    public NativeArray<float> GetCurveData() => allCurveData;
    public NativeArray<BiomeData> GetBiomeData() => biomeData;
    public NativeArray<BiomeDefinition> GetBiomeDefinitions() => biomeDefinitions;
    public NativeArray<DecorationData> GetDecorationData() => decorationData;
    public NativeArray<byte> GetDecorationSpawnBlocks() => decorationSpawnBlocks;
    public int GetWaterLevel() => waterLevel;
    public NativeArray<ClimateGridCell> GetClimateGridCells() => climateGridCells;
    public NativeArray<byte> GetClimateCellBiomes() => climateCellBiomes;
    public NativeArray<float> GetBiomeRarityWeights() => biomeRarityWeights;
    public NativeArray<int> GetBiomePriority() => biomePriority;
    public ClimateGridSO GetClimateGrid() => climateGrid;
    // Debug helpers
    [ContextMenu("Print Terrain Distribution")]
    public void PrintTerrainDistribution()
    {
        if (!isInitialized)
        {
            Debug.LogWarning("BiomeDataManager not initialized yet!");
            return;
        }

        Debug.Log("=== TERRAIN TYPE DISTRIBUTION ===");

        float totalWeight = 0f;
        for (int i = 0; i < terrainTypes.Length; i++)
        {
            if (terrainTypes[i].rarityWeight > 0f)
                totalWeight += terrainTypes[i].rarityWeight;
        }

        for (int i = 0; i < terrainTypes.Length; i++)
        {
            var terrain = terrainTypes[i];
            if (terrain.rarityWeight <= 0f)
            {
                Debug.Log($"Terrain {i} '{terrain.terrainTypeName}': DISABLED (weight = {terrain.rarityWeight})");
                continue;
            }

            float percentage = (terrain.rarityWeight / totalWeight) * 100f;
            string biomeList = string.Join(", ", terrain.allowedBiomes);

            Debug.Log($"Terrain {i} '{terrain.terrainTypeName}': {percentage:F1}% of map " +
                      $"(weight = {terrain.rarityWeight}, biomes: {biomeList})");
        }
    }

    void OnValidate()
    {
        // Check for null references
        if (terrainTypes != null)
        {
            for (int i = 0; i < terrainTypes.Length; i++)
            {
                if (terrainTypes[i] == null)
                {
                    Debug.LogWarning($"Terrain type at index {i} is null!");
                }
            }
        }

        if (biomes != null)
        {
            for (int i = 0; i < biomes.Length; i++)
            {
                if (biomes[i] == null)
                {
                    Debug.LogWarning($"Biome at index {i} is null!");
                }
            }
        }
    }

    private void OnDestroy()
    {
        if (isInitialized)
        {
            if (terrainTypeData.IsCreated) terrainTypeData.Dispose();
            if (allCurveData.IsCreated) allCurveData.Dispose();
            if (terrainAllowedBiomes.IsCreated) terrainAllowedBiomes.Dispose();
            if (biomeData.IsCreated) biomeData.Dispose();
            if (biomeDefinitions.IsCreated) biomeDefinitions.Dispose();
            if (decorationData.IsCreated) decorationData.Dispose();
            if (decorationSpawnBlocks.IsCreated) decorationSpawnBlocks.Dispose();
            if (climateGridCells.IsCreated) climateGridCells.Dispose();
            if (climateCellBiomes.IsCreated) climateCellBiomes.Dispose();
            if (biomeRarityWeights.IsCreated) biomeRarityWeights.Dispose();
            if (biomePriority.IsCreated) biomePriority.Dispose();
        }
    }
}
using System;
using UnityEngine;

/// <summary>
/// How a biome shapes the ground under the region generator. The surface is a height (base +
/// hills) that blends smoothly into neighbouring biomes; an optional FastNoise tree adds 3D shapes
/// (overhangs, spires, arches) on top, sampled relative to that surface and faded by the biome's
/// weight so it shrinks away towards the biome's border.
/// </summary>
[Serializable]
public class RegionShape : ISerializationCallbackReceiver
{
    [Header("Height")]
    [Tooltip("Ground height of this biome in blocks, added to the world's macro height.")]
    public float baseHeight = 0f;

    [Tooltip("Height of the hills in blocks (peak to valley is about twice this).")]
    [Min(0f)] public float hillHeight = 30f;

    [Tooltip("Size of the hills in blocks.")]
    [Min(8f)] public float hillScale = 600f;

    [Tooltip("Noise octaves of the hills: more adds smaller bumps.")]
    [Range(1, 8)] public int hillOctaves = 5;

    [Tooltip("0 = rolling hills, 1 = sharp ridges and wide valleys.")]
    [Range(0f, 1f)] public float ridged = 0f;

    [Tooltip("Terrace height in blocks; 0 = no terraces. Terraces turn slopes into flat steps with cliffs between them.")]
    [Min(0f)] public float terraceStep = 0f;

    [Tooltip("How sharp the terrace cliffs are: 0 = gentle slopes, 1 = sheer walls.")]
    [Range(0f, 1f)] public float terraceSharpness = 0.7f;

    [Header("3D Shapes (optional)")]
    [Tooltip("FastNoise tree for overhangs, spires and other 3D shapes. Must be in BiomeDataManager's terrain types. It is sampled with y measured from this biome's surface.")]
    public TerrainTypeDefinitionSO detailTerrain;

    [Tooltip("How much the 3D shape replaces the height surface at the heart of the biome (it fades out towards the border).")]
    [Range(0f, 1f)] public float detailStrength = 1f;

    [Tooltip("Off: the tree's ground is measured at start (Auto Calibrate). On: use Detail Surface Offset.")]
    public bool overrideSurfaceOffset = false;

    [Tooltip("Height in the tree's own coordinates where its ground sits; it is placed on this biome's surface. Higher sinks the shape into the ground, lower raises it out. Used when Override Surface Offset is on (the log prints the measured value to start from).")]
    public float detailSurfaceOffset = 0f;

    [Tooltip("Off: how far the shape reaches above the surface is measured at start. On: use Detail Range Up.")]
    public bool overrideRangeUp = false;

    [Tooltip("Highest the 3D shape reaches above the surface, in blocks. Nothing is generated above this: too low cuts shapes off flat. Used when Override Range Up is on.")]
    [Min(0f)] public float detailRangeUp = 64f;

    [Tooltip("Off: how far the shape reaches below the surface is measured at start. On: use Detail Range Down.")]
    public bool overrideRangeDown = false;

    [Tooltip("Deepest the 3D shape reaches below the surface, in blocks (caves, undercuts). Used when Override Range Down is on.")]
    [Min(0f)] public float detailRangeDown = 32f;

    // Older assets: Auto Calibrate off meant all three values were set by hand
    [SerializeField, HideInInspector] bool autoCalibrate = true;

    public void OnBeforeSerialize() { }

    public void OnAfterDeserialize()
    {
        if (autoCalibrate)
            return;
        autoCalibrate = true;
        overrideSurfaceOffset = overrideRangeUp = overrideRangeDown = true;
    }

    /// <summary>Where the tree's ground sits and how far it reaches: measured, except the overridden values.</summary>
    public DetailCalibration.Placement ResolveDetail(float frequency, int seed) =>
        DetailCalibration.Resolve(detailTerrain, frequency, seed,
            overrideSurfaceOffset, detailSurfaceOffset, overrideRangeUp, detailRangeUp, overrideRangeDown, detailRangeDown);

    [Tooltip("Multiplier on the tree's output so its density per block matches the world's density scale.")]
    [Min(0.001f)] public float detailDensityScale = 1f;

    [Header("Water")]
    [Tooltip("How much rain this biome feeds into rivers and lakes (1 = normal, 0 = none: deserts).")]
    [Range(0f, 3f)] public float rainfall = 1f;

    [Header("Sub-Biomes")]
    [Tooltip("Layers that vary the biome inside its region: forest thickets, rocky patches, plains, raised hills. They overlap freely; each one's effects scale with its mask.")]
    public SubBiomeLayer[] subBiomes = new SubBiomeLayer[0];
}

public enum SubBiomeDecorationMode : byte
{
    [Tooltip("The layer's decorations spawn on top of the biome's (which Decoration Multiplier still scales).")]
    Add,
    [Tooltip("Where the layer is, only its decorations spawn.")]
    Replace
}

public enum SubBiomeKind : byte
{
    [Tooltip("Smooth noise: wandering bands and blobs that cover a share of the biome.")]
    Continuous,
    [Tooltip("Separate rounded patches, one per cell at most.")]
    Patches
}

/// <summary>
/// A variation inside a biome. Its mask (0..1) comes from noise (Continuous) or scattered
/// rounded patches (Patches); everything it changes scales with that mask and with the biome's
/// own weight, so layers fade out at biome borders and blend where they overlap.
/// </summary>
[Serializable]
public class SubBiomeLayer : ISerializationCallbackReceiver
{
    public string name = "Layer";
    public SubBiomeKind kind = SubBiomeKind.Continuous;

    [Tooltip("Continuous: size of the noise features in blocks. Patches: spacing of patch cells in blocks (patches are up to about this wide).")]
    [Min(8f)] public float scale = 800f;

    [Tooltip("Share of the biome covered (Continuous), or share of cells holding a patch (Patches).")]
    [Range(0f, 1f)] public float coverage = 0.3f;

    [Tooltip("How soft the edges are: 0 = sharp, 1 = very gradual.")]
    [Range(0.01f, 1f)] public float softness = 0.3f;

    [Header("Effects (at full mask)")]
    [Tooltip("Blocks added to the ground height (negative = basins).")]
    public float heightOffset = 0f;

    [Tooltip("Multiplier on the biome's hill height: 0 = flat plains, 2 = twice as hilly.")]
    [Range(0f, 4f)] public float hillMultiplier = 1f;

    [Tooltip("Multiplier on the 3D shape strength (the layer's own shape, or the biome's).")]
    [Range(0f, 2f)] public float detailMultiplier = 1f;

    [Tooltip("Multiplier on decoration spawn chances: 0 = clearings, 3 = thickets.")]
    [Range(0f, 5f)] public float decorationMultiplier = 1f;

    [Tooltip("Block for gentle surfaces where this layer dominates (Air = keep the biome's).")]
    public BlockType surfaceBlock = BlockType.Air;

    [Header("Decorations")]
    [Tooltip("Add: the layer's decorations spawn on top of the biome's. Replace: where the layer is, only its decorations spawn. The edge is dithered by the layer's mask.")]
    public SubBiomeDecorationMode decorationMode = SubBiomeDecorationMode.Add;

    [Tooltip("This layer's own decorations (not scaled by Decoration Multiplier, which applies to the biome's).")]
    public DecorationSet decorations = new DecorationSet();

    /// <summary>True when the layer changes which decorations spawn.</summary>
    public bool HasDecorations => decorationMode == SubBiomeDecorationMode.Replace || (decorations != null && !decorations.IsEmpty);

    [Header("3D Shape (optional)")]
    [Tooltip("This layer's own FastNoise tree for 3D shapes; where the layer is, it takes over from the biome's shape by the layer's mask (overlapping layers with their own shapes share it). None = the biome's shape. Must be in BiomeDataManager's terrain types.")]
    public TerrainTypeDefinitionSO detailTerrain;

    [Tooltip("How much this layer's 3D shape replaces the height surface where the layer is at full mask.")]
    [Range(0f, 1f)] public float detailStrength = 1f;

    [Tooltip("Off: the tree's ground is measured at start (Auto Calibrate). On: use Detail Surface Offset.")]
    public bool overrideSurfaceOffset = false;

    [Tooltip("Height in the tree's own coordinates where its ground sits; it is placed on the biome's surface. Higher sinks the shape into the ground, lower raises it out. Used when Override Surface Offset is on (the log prints the measured value to start from).")]
    public float detailSurfaceOffset = 0f;

    [Tooltip("Off: how far the shape reaches above the surface is measured at start. On: use Detail Range Up.")]
    public bool overrideRangeUp = false;

    [Tooltip("Highest the 3D shape reaches above the surface, in blocks. Nothing is generated above this: too low cuts shapes off flat. Used when Override Range Up is on.")]
    [Min(0f)] public float detailRangeUp = 64f;

    [Tooltip("Off: how far the shape reaches below the surface is measured at start. On: use Detail Range Down.")]
    public bool overrideRangeDown = false;

    [Tooltip("Deepest the 3D shape reaches below the surface, in blocks (caves, undercuts). Used when Override Range Down is on.")]
    [Min(0f)] public float detailRangeDown = 32f;

    // Older assets: Auto Calibrate off meant all three values were set by hand
    [SerializeField, HideInInspector] bool autoCalibrate = true;

    public void OnBeforeSerialize() { }

    public void OnAfterDeserialize()
    {
        if (autoCalibrate)
            return;
        autoCalibrate = true;
        overrideSurfaceOffset = overrideRangeUp = overrideRangeDown = true;
    }

    /// <summary>Where the tree's ground sits and how far it reaches: measured, except the overridden values.</summary>
    public DetailCalibration.Placement ResolveDetail(float frequency, int seed) =>
        DetailCalibration.Resolve(detailTerrain, frequency, seed,
            overrideSurfaceOffset, detailSurfaceOffset, overrideRangeUp, detailRangeUp, overrideRangeDown, detailRangeDown);

    [Tooltip("Multiplier on the tree's output so its density per block matches the world's density scale.")]
    [Min(0.001f)] public float detailDensityScale = 1f;
}

/// <summary>
/// 3D shape trees are numbered as detail sources: a biome's own tree is its biome ID, a sub-biome
/// layer's own tree comes after all biome IDs, in the order Layers lists the layers.
/// </summary>
public static class RegionDetailSources
{
    public const byte None = 255;

    /// <summary>Every sub-biome layer with its index (null biomes and layers skipped).</summary>
    public static System.Collections.Generic.IEnumerable<(BiomeDefinitionSO biome, SubBiomeLayer layer, int index)> Layers(BiomeDataManager biomeDataManager)
    {
        int n = 0;
        foreach (var biome in biomeDataManager.biomes)
        {
            var subs = biome != null ? biome.regionShape?.subBiomes : null;
            if (subs == null)
                continue;
            foreach (var layer in subs)
                if (layer != null)
                    yield return (biome, layer, n++);
        }
    }

    /// <summary>The source of a layer's own tree, or None when it has none (or too many sources exist).</summary>
    public static byte LayerSource(int biomeCount, SubBiomeLayer layer, int index)
    {
        if (layer.detailTerrain == null || layer.detailStrength <= 0f)
            return None;
        int id = biomeCount + index;
        return id < None ? (byte)id : None;
    }
}

/// <summary>Job safe settings of one detail source (a biome's or a sub-biome layer's 3D shape tree).</summary>
public struct DetailSourceData
{
    public byte hasDetail;
    public float strength;
    public float rangeUp;
    public float rangeDown;
    public byte overrideGradient;   // 1 = the tree's own gradient thresholds, else the biome's
    public float gradient1, gradient2;
}

/// <summary>Job safe copy of a SubBiomeLayer.</summary>
public struct SubBiomeData
{
    public byte kind;
    public uint seed;
    public float invScale;
    public float threshold;        // Continuous: noise value at the middle of the edge
    public float coverage;         // Patches: chance a cell holds a patch
    public float softness;
    public FbmSimplexNoise2D noise;   // Continuous mask, or patch outline wobble
    public float heightOffset;
    public float hillMultiplier;
    public float detailMultiplier;
    public float decorationMultiplier;
    public byte surfaceBlock;
    public byte detailSource;      // the layer's own 3D shape (RegionDetailSources.None = the biome's)
    public byte hasDecorations;    // 1 = the layer's decorations apply where it is (dithered by its mask)
}

/// <summary>Job safe copy of a biome's RegionShape, indexed by biome ID.</summary>
public struct BiomeShapeData
{
    public byte defined;            // 1 = a BiomeDefinitionSO exists for this ID
    public float baseHeight;
    public float hillHeight;
    public float ridged;
    public float terraceStep;
    public float terraceExponent;   // from terraceSharpness
    public FbmSimplexNoise2D hillNoise;
    public FbmSimplexNoise2D ridgeNoise;

    public byte hasDetail;          // 1 = a 3D shape tree is configured
    public float detailStrength;
    public float detailRangeUp;
    public float detailRangeDown;

    public float rainfall;
    public byte sedimentBlock;
    public float gradient1, gradient2;   // the biome's gradient thresholds
    public int subStart, subCount;  // range in RegionSystem's SubBiomeData array
}

/// <summary>
/// One column (x, z) of the region generator's 2D data: the blended surface, which biome's block
/// palette applies, and up to three biomes whose 3D shape trees apply, with their weights.
/// </summary>
public struct ColumnSample
{
    public float height;     // blended surface height (world y)
    public float invNorm;    // 1 / sqrt(1 + slope^2): turns height difference into distance from the surface
    public float bandMin;    // world y range where 3D shapes can differ from the height surface
    public float bandMax;
    public byte palette;     // biome ID for blocks and decoration
    public byte detail0, detail1, detail2, detail3;   // detail sources (RegionDetailSources) with 3D shapes here (255 = none)
    public float detailW0, detailW1, detailW2, detailW3;   // their weight * strength
    public float waterY;     // water fills air at or below this world y (rivers, lakes, the water level)
    public float decoScale;  // decoration chance multiplier from sub-biomes
    public byte surfaceOverride;   // gentle surface block (0 = biome palette)
    public float gradient1, gradient2;   // gradient thresholds inside the band (3D shapes' blended with the palette biome's)

    public const byte None = 255;

    public bool HasDetail => detail0 != None;
    public float DetailWeight => detailW0 + detailW1 + detailW2 + detailW3;
    public bool Uses(int source) => detail0 == source || detail1 == source || detail2 == source || detail3 == source;
}

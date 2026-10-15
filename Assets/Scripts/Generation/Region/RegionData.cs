using System;
using UnityEngine;

/// <summary>
/// How a biome shapes the ground under the region generator. The surface is a height (base +
/// hills) that blends smoothly into neighbouring biomes; an optional FastNoise tree adds 3D shapes
/// (overhangs, spires, arches) on top, sampled relative to that surface and faded by the biome's
/// weight so it shrinks away towards the biome's border.
/// </summary>
[Serializable]
public class RegionShape
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

    [Tooltip("Height in the tree's own coordinates where its ground sits; it is placed on this biome's surface.")]
    public float detailSurfaceOffset = 0f;

    [Tooltip("Highest the 3D shape reaches above the surface, in blocks. Nothing is generated above this: too low cuts shapes off flat.")]
    [Min(0f)] public float detailRangeUp = 64f;

    [Tooltip("Deepest the 3D shape reaches below the surface, in blocks (caves, undercuts).")]
    [Min(0f)] public float detailRangeDown = 32f;

    [Tooltip("Multiplier on the tree's output so its density per block matches the world's density scale.")]
    [Min(0.001f)] public float detailDensityScale = 1f;
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
    public byte detail0, detail1, detail2;   // biome IDs with 3D shapes here (255 = none)
    public float detailW0, detailW1, detailW2;   // their weight * strength

    public const byte None = 255;

    public bool HasDetail => detail0 != None;
    public float DetailWeight => detailW0 + detailW1 + detailW2;
}

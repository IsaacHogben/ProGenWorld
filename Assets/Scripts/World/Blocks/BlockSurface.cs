using UnityEngine;

/// <summary>
/// The look of a block: a grey texture coloured by a palette of up to three colours, with
/// optional painted relief and alpha masks. BlockDatabase entries point at one; several blocks
/// can share a surface, and duplicating the asset makes a variant.
///
/// The grey texture's red channel is the texel's shade (brightness) and its green channel the
/// texel's position along the palette (colour); a plain greyscale image uses its brightness
/// for both. The voxel shader samples the texture at a small and a large scale
/// (VoxelRenderSettings): the large scale colour position picks the patch colour, so patches of
/// the colours spread over many blocks, and the small scale texel shades it between Shade Dark
/// and Shade Light. Colours and texture values are sRGB, like a colour picker.
///
/// BlockTextureArrayBuilder bakes Colour(position, shade) into a lookup texture per block and the
/// patch settings into PatchParams; VoxelSampleBlockTexture combines them the way Evaluate does.
/// </summary>
[CreateAssetMenu(fileName = "BlockSurface", menuName = "World/Block Surface", order = 3)]
public class BlockSurface : ScriptableObject
{
    public const int MaxColours = 3;

    [Tooltip("Detail texture, the size of the block texture array. Red: shade (brightness). Green: position along the colours. A plain greyscale image uses its brightness for both.")]
    public Texture2D greyTexture;

    [Tooltip("1 to 3 colours. A texel's colour position picks along the list: 0 = first colour, 1 = last.")]
    public Color[] colours = { new Color(0.5f, 0.5f, 0.5f, 1f) };

    [Tooltip("Brightness of the darkest texel (multiplies the colour).")]
    [Range(0f, 2f)] public float shadeDark = 0.75f;
    [Tooltip("Brightness of the brightest texel (multiplies the colour).")]
    [Range(0f, 2f)] public float shadeLight = 1.25f;

    [Tooltip("How sharply patches separate into the colours. 1 follows the texture's own colour positions.")]
    [Range(0f, 8f)] public float patchContrast = 1f;
    [Tooltip("Shifts patches towards the first (-) or the last (+) colour.")]
    [Range(-1f, 1f)] public float patchOffset = 0f;
    [Tooltip("0: every texel takes the colour of its patch. 1: each texel uses its own colour position, so the colours also mix inside a block.")]
    [Range(0f, 1f)] public float texelColourVariation = 0f;

    [Tooltip("Painted relief height per texel (red: 1 = surface, 0 = deepest). Empty: from the shade.")]
    public Texture2D reliefMask;
    [Tooltip("Cut-out mask per block face (red > 0.5 = solid), for Stacked blocks such as leaves. Empty: opaque.")]
    public Texture2D alphaMask;

    /// <summary>Raised when a surface is edited, so a running world can refresh its colours.</summary>
    public static event System.Action<BlockSurface> Changed;

    void OnValidate() => Changed?.Invoke(this);

    public int ColourCount => colours == null ? 0 : Mathf.Clamp(colours.Length, 0, MaxColours);

    /// <summary>Palette colour at t (0 = first colour, 1 = last).</summary>
    public Color Ramp(float t)
    {
        int n = ColourCount;
        if (n == 0) return Color.grey;
        if (n == 1) return colours[0];
        if (n == 2) return Color.LerpUnclamped(colours[0], colours[1], t);
        return t < 0.5f
            ? Color.LerpUnclamped(colours[0], colours[1], t * 2f)
            : Color.LerpUnclamped(colours[1], colours[2], t * 2f - 1f);
    }

    /// <summary>sRGB colour at palette position t and shade (texture red) g.</summary>
    public Color Colour(float t, float g)
    {
        var c = Ramp(t) * Mathf.LerpUnclamped(shadeDark, shadeLight, g);
        c.a = 1f;
        return c;
    }

    /// <summary>Patch position from the large scale colour position.</summary>
    public float Patch(float colourLarge) => Mathf.Clamp01((colourLarge - 0.5f) * patchContrast + 0.5f + patchOffset);

    /// <summary>
    /// For the shader: patch = saturate(colourLarge * x + y), texel position = lerp(patch, colourSmall, z).
    /// </summary>
    public Vector4 PatchParams => new Vector4(patchContrast, 0.5f + patchOffset - 0.5f * patchContrast, texelColourVariation, 0f);

    /// <summary>sRGB colour of a small scale texel (shade, colour position) inside the patch picked by colourLarge.</summary>
    public Color Evaluate(float shadeSmall, float colourSmall, float colourLarge)
        => Colour(Mathf.Lerp(Patch(colourLarge), colourSmall, texelColourVariation), shadeSmall);

    /// <summary>sRGB colour of the large scale layer.</summary>
    public Color EvaluateLarge(float shadeLarge, float colourLarge) => Colour(Patch(colourLarge), shadeLarge);

    /// <summary>
    /// Final sRGB colour as the shader blends the two scales in linear space (VoxelRenderSettings
    /// large blend / blend mode). small / large: grey texture pixels (r shade, g colour).
    /// </summary>
    public Color EvaluateBlended(Color small, Color large, float largeBlend, bool multiply)
    {
        Color s = Evaluate(small.r, small.g, large.g).linear;
        Color l = EvaluateLarge(large.r, large.g).linear;
        Color c = multiply
            ? s * Color.LerpUnclamped(Color.white, l * 2f, largeBlend)
            : Color.LerpUnclamped(s, l, largeBlend);
        c.a = 1f;
        return c.gamma;
    }

    /// <summary>
    /// Grey texture pixels coloured as one tile, each texel in the patch of its own colour
    /// position: a flat stand-in for previews that do not render both scales.
    /// </summary>
    public Color[] ColourTile(Color[] greyPixels)
    {
        var result = new Color[greyPixels.Length];
        for (int i = 0; i < greyPixels.Length; i++)
            result[i] = Evaluate(greyPixels[i].r, greyPixels[i].g, greyPixels[i].g);
        return result;
    }
}

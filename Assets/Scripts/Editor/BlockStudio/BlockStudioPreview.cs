#if UNITY_EDITOR
using UnityEngine;

/// <summary>
/// CPU renders of a flat block face for the Block Studio, using the scene's texture variation
/// settings the way VoxelSampleBlockTexture does (point sampled, no lighting).
/// </summary>
public static class BlockStudioPreview
{
    public struct Variation
    {
        public float smallScale, largeScale, largeBlend;
        public bool multiply;
    }

    public static Variation SceneVariation()
    {
        var s = Object.FindFirstObjectByType<VoxelRenderSettings>();
        if (s == null)
            return new Variation { smallScale = 2f, largeScale = 32f, largeBlend = 0.35f };
        return new Variation
        {
            smallScale = Mathf.Max(s.smallScale, 0.01f),
            largeScale = Mathf.Max(s.largeScale, 0.01f),
            largeBlend = s.largeBlend,
            multiply = s.blendMode == VoxelRenderSettings.TextureBlendMode.Multiply
        };
    }

    static int Texel(float blocks, float scale, int size)
    {
        float uv = blocks / scale;
        uv -= Mathf.Floor(uv);
        return Mathf.Min((int)(uv * size), size - 1);
    }

    /// <summary>
    /// Renders blocks x blocks of a palette surface into target. grey: grey texture pixels
    /// (r shade, g colour position). relief (optional) darkens recessed texels.
    /// </summary>
    public static void RenderSurface(Texture2D target, BlockSurface surface, Color[] grey, float[] relief, int size,
                                     float blocks, Variation v)
    {
        int res = target.width;
        var pixels = new Color32[res * res];
        var tS = new int[res];
        var tL = new int[res];
        for (int i = 0; i < res; i++)
        {
            float p = (i + 0.5f) / res * blocks;
            tS[i] = Texel(p, v.smallScale, size);
            tL[i] = Texel(p, v.largeScale, size);
        }

        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
            {
                int s = tS[x] + tS[y] * size;
                int l = tL[x] + tL[y] * size;
                Color c = surface.EvaluateBlended(grey[s], grey[l], v.largeBlend, v.multiply);
                if (relief != null)
                    c *= Mathf.Lerp(0.55f, 1f, relief[s]);
                c.a = 1f;
                pixels[x + y * res] = c;
            }
        target.SetPixels32(pixels);
        target.Apply(false);
    }

    /// <summary>The same area with a legacy full colour texture (for comparing conversions).</summary>
    public static void RenderLegacy(Texture2D target, Color[] texture, int size, float blocks, Variation v)
    {
        int res = target.width;
        var pixels = new Color32[res * res];
        var tS = new int[res];
        var tL = new int[res];
        for (int i = 0; i < res; i++)
        {
            float p = (i + 0.5f) / res * blocks;
            tS[i] = Texel(p, v.smallScale, size);
            tL[i] = Texel(p, v.largeScale, size);
        }

        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
            {
                Color s = texture[tS[x] + tS[y] * size].linear;
                Color l = texture[tL[x] + tL[y] * size].linear;
                Color c = v.multiply ? s * Color.LerpUnclamped(Color.white, l * 2f, v.largeBlend) : Color.LerpUnclamped(s, l, v.largeBlend);
                c = c.gamma;
                c.a = 1f;
                pixels[x + y * res] = c;
            }
        target.SetPixels32(pixels);
        target.Apply(false);
    }

    public static Texture2D NewTexture(int size)
    {
        return new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
    }
}
#endif

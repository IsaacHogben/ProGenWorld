#if UNITY_EDITOR
using UnityEngine;

/// <summary>
/// Turns a wanted colour into the closest grey texture pixel of a palette surface: shade (r, also
/// written to b) and palette position (g) with BlockSurface.Colour(position, shade) nearest to it.
/// Rebuild after the surface's colours or shades change.
/// </summary>
public class PaletteSolver
{
    public enum Affect { ShadeAndColour, ShadeOnly, ColourOnly }

    const int Grid = 64;
    BlockSurface surface;
    Color[] table;

    public void Rebuild(BlockSurface s)
    {
        surface = s;
        table = new Color[Grid * Grid];
        for (int g = 0; g < Grid; g++)
            for (int t = 0; t < Grid; t++)
                table[t + g * Grid] = s.Colour(t / (Grid - 1f), g / (Grid - 1f));
    }

    static float Dist2(Color a, Color b)
    {
        float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
        return dr * dr + dg * dg + db * db;
    }

    /// <summary>The grey texture pixel for want; affect keeps the other channel of before.</summary>
    public Color Solve(Color before, Color want, Affect affect, out float error)
    {
        float bestT = before.g, bestG = before.r, best = float.MaxValue;
        if (surface == null)
        {
            error = 1f;
            return before;
        }

        switch (affect)
        {
            case Affect.ShadeOnly:
                for (int i = 0; i < 256; i++)
                {
                    float g = i / 255f;
                    float d = Dist2(surface.Colour(bestT, g), want);
                    if (d < best) { best = d; bestG = g; }
                }
                break;

            case Affect.ColourOnly:
                for (int i = 0; i < 256; i++)
                {
                    float t = i / 255f;
                    float d = Dist2(surface.Colour(t, bestG), want);
                    if (d < best) { best = d; bestT = t; }
                }
                break;

            default:
                int bi = 0;
                for (int i = 0; i < table.Length; i++)
                {
                    float d = Dist2(table[i], want);
                    if (d < best) { best = d; bi = i; }
                }
                float t0 = (bi % Grid) / (Grid - 1f), g0 = (bi / Grid) / (Grid - 1f);
                bestT = t0; bestG = g0;
                // Refine between the neighbouring grid points.
                const float step = 1f / (Grid - 1f) / 4f;
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        float t = Mathf.Clamp01(t0 + dx * step), g = Mathf.Clamp01(g0 + dy * step);
                        float d = Dist2(surface.Colour(t, g), want);
                        if (d < best) { best = d; bestT = t; bestG = g; }
                    }
                break;
        }

        error = Mathf.Sqrt(best / 3f);
        return new Color(bestG, bestT, bestG, 1f);
    }
}
#endif

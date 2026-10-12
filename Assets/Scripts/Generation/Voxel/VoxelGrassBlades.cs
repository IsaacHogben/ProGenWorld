using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Precomputed grass blade layouts for VoxelGrass.hlsl: Sets layouts of MaxBlades blades, each
/// blade packed into one uint and already fitted to the blade grid (texels per block) and the
/// width / lean settings, so the shader only unpacks bit fields. A grass block picks a set by hash
/// and shows the blades whose rank is below its blade count. A set is 4 uint4s, so the shader
/// reads it in 4 loads and tests its blades in registers.
///
/// Blade bits, keep in sync with the shader:
///   bit 0      card axis (1 = X plane, 0 = Z plane)
///   bits 1-6   plane: texel line of the card inside the block (1 .. texels - 1)
///   bits 7-12  first root texel along the card
///   bits 13-15 root width (texels)
///   bits 16-19 lean + 8 (texels the tip shifts along the card, signed)
///   bits 20-27 height random (0..255 -> 45%..100% of the blade height)
///   bits 28-31 rank: a block with N blades shows ranks 0 .. N - 1
/// </summary>
public static class VoxelGrassBlades
{
    public const int Sets = 256;
    public const int MaxBlades = 16;   // VOXEL_GRASS_MAX_BLADES
    public const int MaxTexels = 63;   // 6 bit plane / along fields

    public static int Length => Sets * MaxBlades / 4;   // uint4s

    public static uint4[] Bake(int texels, int maxWidth, int maxLean)
    {
        texels = Mathf.Clamp(texels, 2, MaxTexels);
        maxWidth = Mathf.Clamp(maxWidth, 1, Mathf.Min(7, texels));
        maxLean = Mathf.Clamp(maxLean, 0, 7);

        var data = new uint4[Length];
        var rng = new Unity.Mathematics.Random(0x6A5Du);
        var ranks = new uint[MaxBlades];

        for (int s = 0; s < Sets; s++)
        {
            // Random ranks, so any blade count picks blades spread over the whole layout.
            for (int i = 0; i < MaxBlades; i++)
                ranks[i] = (uint)i;
            for (int i = MaxBlades - 1; i > 0; i--)
            {
                int j = rng.NextInt(0, i + 1);
                (ranks[i], ranks[j]) = (ranks[j], ranks[i]);
            }

            for (int i = 0; i < MaxBlades; i++)
            {
                uint axisX = rng.NextBool() ? 1u : 0u;
                int plane = rng.NextInt(1, texels);
                int width = rng.NextInt(1, maxWidth + 1);
                int lean = rng.NextInt(0, maxLean + 1) * (rng.NextBool() ? 1 : -1);

                // Keep the whole blade, lean included, inside the block.
                int lo = Mathf.Max(0, -lean);
                int hi = Mathf.Max(lo, texels - width - Mathf.Max(0, lean));
                int along = rng.NextInt(lo, hi + 1);
                uint heightRandom = (uint)rng.NextInt(0, 256);

                uint blade = axisX
                    | ((uint)plane << 1)
                    | ((uint)along << 7)
                    | ((uint)width << 13)
                    | ((uint)(lean + 8) << 16)
                    | (heightRandom << 20)
                    | (ranks[i] << 28);

                int index = s * MaxBlades + i;
                uint4 v = data[index >> 2];
                v[index & 3] = blade;
                data[index >> 2] = v;
            }
        }
        return data;
    }
}

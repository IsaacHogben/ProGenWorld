using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

[CreateAssetMenu(fileName = "BlockDatabase", menuName = "World/Block Table", order = 1)]
public class BlockDatabase : ScriptableObject
{
    [Serializable]
    public struct BlockEntry
    {
        public BlockType type;
        public string name;
        [Tooltip("Colours, relief and alpha mask of the block. Empty: the legacy colour texture below.")]
        public BlockSurface surface;
        [Tooltip("Legacy full colour texture, used when the block has no surface.")]
        public Texture2D texture;

        [Header("Properties")]
        public bool isSolid;
        public bool hasCollision;
        public BlockVisibility visibility;
        [Tooltip("Legacy slice of Resources/VoxelAlphaMasks. Blocks with a surface use the surface's alpha mask instead.")]
        public byte alphaMaskIndex;
        [Tooltip("Drawn on the block's top face when the block above is air.")]
        public SurfaceDecoration surfaceDecoration;

        // Default constructor for new entries
        public BlockEntry(BlockType t)
        {
            type = t;
            name = t.ToString();
            surface = null;
            texture = null;
            isSolid = true;
            hasCollision = true;
            visibility = BlockVisibility.Opaque;
            alphaMaskIndex = 0;
            surfaceDecoration = name.IndexOf("grass", StringComparison.OrdinalIgnoreCase) >= 0
                ? SurfaceDecoration.Grass
                : SurfaceDecoration.None;
        }
    }
    public struct BlockInfoUnmanaged
    {
        public byte id;
        public bool isSolid;
        public bool hasCollision;
        public BlockVisibility visibility;
        public byte alphaMaskIndex;
        public SurfaceDecoration surfaceDecoration;
        public bool hasSurface;   // coloured by a BlockSurface palette (lookup texture) instead of its texture's colours
    }

    [Header("All Block Types")]
    public BlockEntry[] blocks;

    public BlockEntry Get(BlockType type)
    {
        foreach (var block in blocks)
            if (block.type == type)
                return block;
        return default;
    }

    public BlockEntry Get(byte id)
    {
        return Get((BlockType)id);
    }

    /// <summary>Hand made alpha mask array (slice 0 opaque) that legacy alphaMaskIndex values point into.</summary>
    public static Texture2DArray LegacyAlphaMasks => Resources.Load<Texture2DArray>("VoxelAlphaMasks");

    /// <summary>
    /// The distinct alpha masks of the block surfaces and the alpha mask array slice each block
    /// uses. The array is the legacy masks followed by these, so blocks without a surface keep
    /// their legacy alphaMaskIndex.
    /// </summary>
    public List<Texture2D> CollectAlphaMasks(out byte[] slicePerBlock)
    {
        var legacy = LegacyAlphaMasks;
        int firstSlice = legacy != null ? legacy.depth : 1;
        var masks = new List<Texture2D>();
        slicePerBlock = new byte[blocks.Length];
        for (int i = 0; i < blocks.Length; i++)
        {
            var surface = blocks[i].surface;
            if (surface == null)
            {
                slicePerBlock[i] = blocks[i].alphaMaskIndex;
                continue;
            }
            if (surface.alphaMask == null)
                continue;
            int index = masks.IndexOf(surface.alphaMask);
            if (index < 0)
            {
                index = masks.Count;
                masks.Add(surface.alphaMask);
            }
            slicePerBlock[i] = (byte)Mathf.Min(firstSlice + index, 255);
        }
        return masks;
    }

    public NativeArray<BlockInfoUnmanaged> ToNative(Allocator allocator)
    {
        CollectAlphaMasks(out var maskSlices);
        var arr = new NativeArray<BlockInfoUnmanaged>(blocks.Length, allocator);
        for (int i = 0; i < blocks.Length; i++)
        {
            arr[i] = new BlockInfoUnmanaged
            {
                id = (byte)i,
                isSolid = blocks[i].isSolid,
                hasCollision = blocks[i].hasCollision,
                visibility = blocks[i].visibility,
                alphaMaskIndex = maskSlices[i],
                surfaceDecoration = blocks[i].surfaceDecoration,
                hasSurface = blocks[i].surface != null && blocks[i].surface.greyTexture != null
            };
        }
        return arr;
    }

}

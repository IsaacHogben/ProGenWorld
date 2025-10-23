using System;
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
        public Texture2D texture;

        [Header("Properties")]
        public bool isSolid;
        public bool hasCollision;
        public bool isTransparent;

        // Default constructor for new entries
        public BlockEntry(BlockType t)
        {
            type = t;
            name = t.ToString();
            texture = null;
            isSolid = true;
            hasCollision = true;
            isTransparent = false;
        }
    }
    public struct BlockInfoUnmanaged
    {
        public byte id;
        public bool isSolid;
        public bool hasCollision;
        public bool isTransparent;
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

    public NativeArray<BlockInfoUnmanaged> ToNative(Allocator allocator)
    {
        var arr = new NativeArray<BlockInfoUnmanaged>(blocks.Length, allocator);
        for (int i = 0; i < blocks.Length; i++)
        {
            arr[i] = new BlockInfoUnmanaged
            {
                id = (byte)i,
                isSolid = blocks[i].isSolid,
                hasCollision = blocks[i].hasCollision,
                isTransparent = blocks[i].isTransparent
            };
        }
        return arr;
    }

}

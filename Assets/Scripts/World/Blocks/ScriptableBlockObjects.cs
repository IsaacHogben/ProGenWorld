using UnityEngine;
using System;

[CreateAssetMenu(fileName = "BlockDatabase", menuName = "World/Block Table", order = 1)]
public class BlockDatabase : ScriptableObject
{
    [Serializable]
    public struct BlockEntry
    {
        public BlockType type;
        public string name;
        public Material material;

        [Header("Properties")]
        public bool isSolid;
        public bool hasCollision;
        public bool isTransparent;

        // Default constructor for new entries
        public BlockEntry(BlockType t)
        {
            type = t;
            name = t.ToString();
            material = null;
            isSolid = true;
            hasCollision = true;
            isTransparent = false;
        }
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
}
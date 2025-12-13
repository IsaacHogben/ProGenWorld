public enum BlockType : byte
{
    Air = 0,
    Water = 1,
    Stone = 2,
    Dirt = 3,
    Grass = 4,
    Log = 5,
    Leaves = 6,
}

public enum BlockVisibility
{
    Opaque,      // Solid, blocks all visibility (stone, dirt, grass)
    Translucent, // Semi-transparent, visible but see-through (water, glass, ice)
    Invisible  // Fully transparent, invisible (air)
}


public enum BlockType : byte
{
    Air,

    Stone,
    DarkStone,
    PearlStone,

    Dirt,
    CrackedDirt,

    Grass,
    Grass_yellow,
    Grass_low,

    Log,
    Leaves,

    Sand,
    PearlSand,

    Water
}

public enum BlockVisibility
{
    Opaque,      // Solid, blocks all visibility (stone, dirt, grass)
    Translucent, // Semi-transparent, visible but see-through (water, glass, ice)
    Invisible,  // Fully transparent, invisible (air)
    Stacked     // Semi-transparent but will show adjacent blocks of the same type
}


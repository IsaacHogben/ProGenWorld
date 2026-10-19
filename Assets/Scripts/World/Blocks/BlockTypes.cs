public enum BlockType : byte
{
    Air,

    Stone,
    DarkStone,
    PearlStone,
    Slate,

    Dirt,
    RedDirt,
    DarkDirt,
    CrackedDirt,
    Pebbles,
    Gravel,

    Grass,
    Grass_yellow,
    Grass_red,
    Grass_low,

    Log,
    PineLeaves,

    Sand,
    PearlSand,

    Fungus_cap,
    Fungus_stem,
    Fungus_mycelium,

    Water,
    RedSandStone,
    SandStone,

    RedLeaves,
    WillowLeaves,

    City_Surface,
    City_Structure,
    City_Overgrowth,

    Wallpaper,
    Carpet,

    CherryBlossom,
    CherryBlossomPale,
    CherryBark,
    Grass_feilds,
    OakLeaves
}

public enum BlockVisibility
{
    Opaque,      // Solid, blocks all visibility (stone, dirt, grass)
    Translucent, // Semi-transparent, visible but see-through (water, glass, ice)
    Invisible,  // Fully transparent, invisible (air)
    Stacked     // Semi-transparent but will show adjacent blocks of the same type
}

// Drawn by the voxel shader on a block's top face when the block above is air (no extra blocks).
public enum SurfaceDecoration : byte
{
    None,
    Grass        // Texel sized blades, coloured by the block's own texture
}


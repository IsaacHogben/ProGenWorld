using System;

[Flags]
public enum OpenFaces : byte
{
    None = 0,
    PosX = 1 << 0,
    NegX = 1 << 1,
    PosY = 1 << 2,
    NegY = 1 << 3,
    PosZ = 1 << 4,
    NegZ = 1 << 5,
}

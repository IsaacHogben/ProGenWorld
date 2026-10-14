// VoxelWind.hlsl
// Wind for grass blades and leaves. Included by VoxelRaymarchCore.hlsl.
//
// A wind direction with gusts: two crossed waves over the world drifting downwind, with a slow
// sway riding on top (cheap enough for every leaf voxel a ray touches). Time advances in steps (_VoxelWindAnim.y per second) so movement reads as
// pixel animation, and everything that moves does so by whole texels:
//   - grass blades bend downwind, most at the tip (VoxelGrass.hlsl);
//   - leaf alpha masks shift in 4x4 texel clusters, each fluttering with its own phase.
// Nothing leaves its block, so draw boxes and depth stay valid, and every pass of a frame sees
// the same time, so the depth prepass and the forward pass agree.

#ifndef PROGENWORLD_VOXEL_WIND_INCLUDED
#define PROGENWORLD_VOXEL_WIND_INCLUDED

float4 _VoxelWind;      // xy: direction (xz, unit), z: gust size (blocks), w: gust speed (blocks / s)
float4 _VoxelWindAnim;  // x: strength (0 = off), y: steps per second (0 = smooth), z: grass sway (texels), w: leaf flutter (texels)
float  _VoxelLeafFlutterDistance;  // leaves farther from the camera than this (world units) are still

bool VoxelWindEnabled()
{
    return _VoxelWindAnim.x > 0.0;
}

float VoxelWindTime()
{
    float rate = _VoxelWindAnim.y;
    return rate > 0.0 ? floor(_Time.y * rate) / rate : _Time.y;
}

uint VoxelWindHash(int2 p)
{
    uint h = ((uint)p.x * 0x8DA6B343u) ^ ((uint)p.y * 0xD8163841u);
    h ^= h >> 16; h *= 0x7FEB352Du;
    h ^= h >> 15; h *= 0x846CA68Bu;
    h ^= h >> 16;
    return h;
}

float VoxelWindUnit(uint h)
{
    return (float)(h & 0xFFFFu) / 65535.0;
}

// Wind at a world position (xz), 0 .. strength: drifting gusts times a slow sway. The gusts are
// two crossed waves (one along the wind, travelling with it, one across it) instead of a noise.
float VoxelWindAt(float2 xz, float t)
{
    float2 dir = _VoxelWind.xy;
    float k = 6.2831853 / max(_VoxelWind.z, 0.5);
    float down = dot(xz, dir), across = dot(xz, float2(-dir.y, dir.x));
    float gust = 0.5 + 0.3 * sin((down - _VoxelWind.w * t) * k) + 0.2 * sin(across * k * 0.6 + t * 0.7);
    float sway = sin(t * 2.5 + down * 0.4);
    return _VoxelWindAnim.x * (0.2 + 0.8 * gust) * (0.7 + 0.3 * sway);
}

// Grass: how far (texels) blade tips on the block at world xz bend along their card, for cards in
// X planes (x, along = z) and in Z planes (y, along = x). Cards across the wind show little of
// it: they are flat.
float2 VoxelWindGrassBend(float2 blockXZ)
{
    if (!VoxelWindEnabled() || _VoxelWindAnim.z <= 0.0)
        return 0.0;
    return _VoxelWindAnim.z * VoxelWindAt(blockXZ + 0.5, VoxelWindTime()) * _VoxelWind.yx;
}

// Leaves: offset (in face uv, 0..1 per voxel face) of the alpha mask at a world position on a
// face of the given axis. Moves 4x4 texel clusters by whole mask texels, back and forth along the
// wind as it lies in the face (horizontal on side faces).
float2 VoxelWindLeafOffset(float3 posWS, int axis, float faceSize)
{
    if (!VoxelWindEnabled() || _VoxelWindAnim.w <= 0.0
        || distance(posWS, GetCameraPositionWS()) > _VoxelLeafFlutterDistance)
        return 0.0;

    float w, h, slices;
    _AlphaMasks.GetDimensions(w, h, slices);
    float texels = w;

    float2 face = (axis == 0 ? posWS.zy : (axis == 1 ? posWS.xz : posWS.xy)) / faceSize;
    int2 cluster = (int2)floor(face * texels / 4.0);
    float t = VoxelWindTime();
    float phase = VoxelWindUnit(VoxelWindHash(cluster + int2(axis * 7919, 0x51ED))) * 6.2831853;
    float flutter = _VoxelWindAnim.w * VoxelWindAt(posWS.xz, t) * sin(t * 6.0 + phase);
    float shift = round(flutter);

    float2 dir = axis == 0 ? float2(_VoxelWind.y, 0.0) : (axis == 1 ? _VoxelWind.xy : float2(_VoxelWind.x, 0.0));
    return round(dir * shift) / texels;
}

#endif

// VoxelLeafMask.hlsl
// Leaves as crossed planes. Included by VoxelRaymarchCore.hlsl after struct VoxelHit.
//
// Near the camera (level 0) a leaf block is not a cube but three planes crossing at its centre,
// one per axis, larger than the block (_VoxelLeafCross.x blocks across), so they reach into the
// air around it. Each plane is a rounded, lobed pixel art outline: solid in the middle, more and
// more see-through texels towards the edge. Past the see-through distance those texels are
// filled with a dark leaf colour instead, so rays stop at the first plane.
//
// Every plane lies on a central plane of its block, and inside any voxel the only planes are on
// that voxel's central planes: its own (a leaf), or those of the leaves beside it in the plane
// reaching in. The pack job marks the air next to leaf blocks (VoxelGpuFormat.LeafMarker), so the
// march tests planes only in leaf and marked voxels.

#ifndef PROGENWORLD_VOXEL_LEAF_MASK_INCLUDED
#define PROGENWORLD_VOXEL_LEAF_MASK_INCLUDED

float4 _VoxelLeafTones;   // x: 1 = flat colour leaves, y: sunlight bands, z: sparkle (share of texels one band brighter), w: dark fill brightness
float4 _VoxelLeafCross;   // x: plane size (blocks, 0 = cubes), y: see-through distance (world units), z: edge start (share of the radius), w: inner holes
float4 _VoxelLeafLight;   // x: light wrap, y: shadow band lift (share of the sun), z: colour saturation, w: colour luminance (linear)
float4 _VoxelLeafLight2;  // x: fill darkness (0 = filled texels take the plane's tone, 1 = the dark fill)
float4 _VoxelLeafSunTint;      // rgb: colour multiplier of the sunlit band
float4 _VoxelLeafShadowTint;   // rgb: colour multiplier of the shadowed band and the dark fill
float4 _VoxelLeafClumps;       // x: clump size (texels), y: share of lighter clumps, z: share of darker clumps, w: 1 = on leaf planes too
float  _VoxelLeafSelfShadow;
float  _VoxelLeafSeeThroughDepth;   // camera rays see through this many leaf blocks, then fill   // 1 = a leaf block's planes shadow each other

// Set by a shadow ray's caller (VoxelLeafIgnoreOwnShadow) while it traces from a leaf plane: xyz
// the world centre of its leaf block, whose planes the ray then ignores; w: 1 = active.
static float4 gVoxelLeafShadowOwner = 0;

#define VOXEL_LEAF_LIT  1u   // VoxelHit.leaf: a leaf plane texel
#define VOXEL_LEAF_FILL 2u   // VoxelHit.leaf: a see-through texel filled with the dark leaf colour

bool VoxelLeafFlat() { return _VoxelLeafTones.x > 0.5; }

bool VoxelLeafCrossOn() { return _VoxelLeafCross.x > 0.0; }

bool VoxelIsLeaf(uint id) { return VoxelBlockVisibility(id) == VOXEL_VIS_STACKED; }

float VoxelLeafTexelSize() { return max(_VoxelTexVariation.x, 0.01) / max(_VoxelTexelCount, 1.0); }

float3 VoxelLeafHash3(int3 p)
{
    uint3 v = (uint3)p * uint3(1597334677u, 3812015801u, 2798796415u);
    v = (v.x ^ v.y ^ v.z) * uint3(1597334677u, 3812015801u, 2798796415u);
    return (float3)(v >> 8) / 16777216.0;
}

// Plane of axis a of leaf n at uv (voxels from n's centre, along the two in-plane axes), texel
// key: 0 = outside its outline, 1 = leaf, 2 = see-through texel inside the outline.
uint VoxelLeafPlaneCover(int3 n, int a, float2 uv, int3 texelKey)
{
    if (min(abs(uv.x), abs(uv.y)) > 0.5)
        return 0u;   // reaches into the face neighbours only, not the diagonal ones
    float h = VoxelLeafHash3(n * 3 + a).x;
    float radius = 0.5 * _VoxelLeafCross.x * (0.86 + 0.14 * sin(5.0 * atan2(uv.y, uv.x) + 6.2832 * h));
    float e = length(uv) / radius;
    if (e >= 1.0)
        return 0u;
    float holes = max(smoothstep(saturate(_VoxelLeafCross.z), 1.0, e), _VoxelLeafCross.w);
    return VoxelLeafHash3(texelKey).x < holes ? 2u : 1u;
}

// Leaf plane at p on the central plane of axis a of voxel x (id xId: a leaf, or air marked next
// to leaves). fill: see-through texels are drawn as dark fill. Returns 0 (nothing),
// VOXEL_LEAF_LIT or VOXEL_LEAF_FILL; owner / leafId: the leaf whose plane it is.
uint VoxelLeafCrossAt(VoxelChunk c, int3 x, uint xId, int a, float3 p, bool fill, out int3 owner, out uint leafId)
{
    owner = x;
    leafId = 0u;
    int ua = a == 0 ? 1 : 0;
    int wa = a == 2 ? 1 : 2;
    float ts = VoxelLeafTexelSize() / c.originSize.w;   // texel size in voxels
    int2 texel = (int2)floor(float2(p[ua], p[wa]) / ts);
    float2 st = ((float2)texel + 0.5) * ts - float2(x[ua], x[wa]);   // texel centre, 0..1 across x
    int3 texelKey = int3(texel, x[a] * 3 + a);
    float reach = 0.5 * _VoxelLeafCross.x - 0.5;

    // x's own plane, then the planes of the leaves beside it (the nearer one along each in-plane
    // axis) reaching in. Looped so the cover test is inlined once.
    uint shade = 0u;
    [loop]
    for (int k = 0; k < 3; k++)
    {
        int3 n = x;
        uint id = xId;
        if (k > 0)
        {
            float f = k == 1 ? st.x : st.y;
            if (min(f, 1.0 - f) >= reach)
                continue;
            n = x + VoxelAxisMask(k == 1 ? ua : wa) * (f < 0.5 ? -1 : 1);
            id = VoxelFetchAny(c, 0u, n);
        }
        if (!VoxelIsLeaf(id))
            continue;
        float2 uv = st - 0.5 - float2(n[ua] - x[ua], n[wa] - x[wa]);
        uint cover = VoxelLeafPlaneCover(n, a, uv, texelKey);
        if (cover == 1u || (cover == 2u && fill && shade == 0u))
        {
            owner = n;
            leafId = id;
            shade = cover == 1u ? VOXEL_LEAF_LIT : VOXEL_LEAF_FILL;
            if (cover == 1u)
                break;
        }
    }
    return shade;
}

// Before the sun shadow ray of a leaf plane hit (leaf block owner of chunk c): without leaf self
// shadowing, the ray ignores the planes of that block.
void VoxelLeafIgnoreOwnShadow(VoxelChunk c, int3 owner)
{
    gVoxelLeafShadowOwner = float4(c.originSize.xyz + ((float3)owner + 0.5) * c.originSize.w,
                                   _VoxelLeafSelfShadow > 0.5 ? 0.0 : 1.0);
}

// Shadow ray normal of a leaf with lighting normal n: tilted towards the sun by the light wrap,
// so every leaf the wrapped light reaches traces its shadow (VoxelSunShadow skips back faces).
float3 VoxelLeafShadowNormal(float3 n, float3 sunDir)
{
    return normalize(n + sunDir * _VoxelLeafLight.x);
}

bool VoxelLeafShadowIgnored(VoxelChunk c, int3 owner)
{
    float3 centre = c.originSize.xyz + ((float3)owner + 0.5) * c.originSize.w;
    return gVoxelLeafShadowOwner.w > 0.5 && all(abs(centre - gVoxelLeafShadowOwner.xyz) < 0.25 * c.originSize.w);
}

// Nearest leaf plane hit by the ray o + d t inside voxel x between tEnter and tExit (level 0
// voxels of c). Texels are see-through before fillFrom (ray t), filled after it.
uint VoxelLeafCrossCell(VoxelChunk c, int3 x, uint xId, float3 o, float3 d, float3 invD, float tEnter, float tExit,
                        float fillFrom, inout VoxelHit hit)
{
    float3 tp = ((float3)x + 0.5 - o) * invD;
    float last = tEnter - 1e-5;
    [loop]
    for (int k = 0; k < 3; k++)
    {
        float t = 3.4e38;
        int a = -1;
        [unroll]
        for (int j = 0; j < 3; j++)
        {
            if (tp[j] > last && tp[j] < t)
            {
                t = tp[j];
                a = j;
            }
        }
        if (a < 0 || t >= tExit)
            break;
        last = t;

        float3 p = o + d * t;
        int3 owner; uint leafId;
        uint shade = VoxelLeafCrossAt(c, x, xId, a, p, t >= fillFrom, owner, leafId);
        if (shade != 0u && !VoxelLeafShadowIgnored(c, owner))
        {
            hit.t = t; hit.id = leafId; hit.axis = a; hit.posVoxel = p;
            hit.leaf = shade; hit.leafOwner = owner; hit.occlusion = 1.0;
            return shade;
        }
    }
    return 0u;
}

// Rebuilds a leaf plane hit from a surface point pv (level 0 voxels of c) on a plane of the given
// axis. False when pv is not on a leaf plane. camDist: unused (kept for the call sites).
bool VoxelLeafResolve(VoxelChunk c, float3 pv, int axis, float camDist, inout VoxelHit hit)
{
    if (!VoxelLeafCrossOn() || abs(frac(pv[axis]) - 0.5) > 0.02)
        return false;
    int3 x = (int3)floor(pv);
    uint xId = VoxelFetchAny(c, 0u, x);
    if (!VoxelIsLeaf(xId) && xId != VOXEL_ID_LEAF_HALO)
        return false;
    int3 owner; uint leafId;
    // A surface the prepass found on a see-through texel was a filled one (past the see-through
    // distance or depth), or the ray would have passed it.
    uint shade = VoxelLeafCrossAt(c, x, xId, axis, pv, true, owner, leafId);
    if (shade == 0u)
        return false;
    hit.id = leafId; hit.axis = axis; hit.posVoxel = pv;
    hit.leaf = shade; hit.leafOwner = owner; hit.occlusion = 1.0;
    return true;
}

// Leaf clump texture of flat colour leaves: -1, 0 or +1 band for the block texture texel at
// posWS (world, just behind the surface). Clumps of a few texels on a 3D world grid (shared by
// adjacent faces), their borders jittered per texel so they read as pixel art leaf clusters.
// Texels of the hit's detail level (twice as big per level), so far clumps stay a few pixels
// wide instead of averaging out.
float VoxelLeafClumpShift(float3 posWS, uint level)
{
    float ts = VoxelLeafTexelSize() * VoxelLevelScale(level);
    float size = max(_VoxelLeafClumps.x, 1.0);
    int3 texel = (int3)floor(posWS / ts);
    float3 jitter = VoxelLeafHash3(texel) * (size * 0.6);
    float v = VoxelLeafHash3((int3)floor(((float3)texel + jitter) / size) + int3(7919, 104729, 1299709)).x;
    return v < _VoxelLeafClumps.z ? -1.0 : (v > 1.0 - _VoxelLeafClumps.y ? 1.0 : 0.0);
}

// Leaf voxel of the given detail level behind a surface point (world) with face normal n, for
// VoxelLeafOutward on leaf cube faces.
int3 VoxelLeafBlockAt(VoxelChunk c, uint level, float3 posWS, float3 n)
{
    return (int3)floor((posWS - n * 0.01 - c.originSize.xyz) / (c.originSize.w * VoxelLevelScale(level)));
}

// Detail level the voxel draw cells around a camera distance use (VoxelSelectLevel, per pixel
// instead of per cell), for passes that do not know the draw cell.
uint VoxelLevelAtDistance(VoxelChunk c, float dist)
{
    if (_VoxelLodDistances.x <= 0.0)
        return VoxelMinLevel(c);
    uint level = dist > _VoxelLodDistances.y ? 2u : (dist > _VoxelLodDistances.x ? 1u : 0u);
    if (_VoxelLodDistances.z > 0.0 && dist > _VoxelLodDistances.z)
        level = 3u;
    return VoxelClampLevel(c, level);
}

// Lighting normal of a leaf voxel v of c at a detail level: away from the canopy, towards the
// open sides of the voxel, tilted up a little. All three planes of a block share it, so a canopy
// reads as one shape with sunlit tops and dark undersides instead of three tones per block. Far
// trees are drawn from coarse levels (whose chunks may not keep level 0), so their neighbours are
// read at that level.
float3 VoxelLeafOutward(VoxelChunk c, uint level, int3 v)
{
    float3 n = float3(0.0, 0.25, 0.0);
    [loop]
    for (int k = 0; k < 6; k++)
    {
        int3 dir = VoxelAxisMask(k >> 1) * ((k & 1) ? 1 : -1);
        uint vis = VoxelBlockVisibility(VoxelFetchAny(c, level, v + dir));
        if (vis != VOXEL_VIS_OPAQUE && vis != VOXEL_VIS_STACKED)
            n += (float3)dir;
    }
    return normalize(n);
}

#endif

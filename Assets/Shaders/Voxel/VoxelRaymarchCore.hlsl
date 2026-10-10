// VoxelRaymarchCore.hlsl
// Shared data layout + ray traversal for the raymarched voxel terrain.
//
// Every chunk is split into draw cells (16x16x16 voxels). Each non-empty cell is drawn as one
// box (36 procedural vertices) shrunk to the cell's non-air content. The pixel shader casts a
// ray into the box and walks the voxel data with a two level DDA:
//   - Brick level (8x8x8). Uniform bricks are stored inline in the brick table and are
//     crossed in a single step (air, water, solid stone...).
//   - Voxel level inside mixed bricks, whose voxels live in a shared page pool
//     (1 page = 512 voxels = 128 uints, 1 byte per voxel).
//
// Data written by VoxelWorldRenderer.cs - keep the layouts in sync.

#ifndef PROGENWORLD_VOXEL_RAYMARCH_CORE_INCLUDED
#define PROGENWORLD_VOXEL_RAYMARCH_CORE_INCLUDED

#define VOXEL_VIS_OPAQUE      0u
#define VOXEL_VIS_TRANSLUCENT 1u
#define VOXEL_VIS_INVISIBLE   2u
#define VOXEL_VIS_STACKED     3u

#define VOXEL_BRICK_UNIFORM   0x80000000u
#define VOXEL_BRICK_DENSE     0x40000000u // mixed brick that is at least half solid (coarse shadows)
#define VOXEL_PAGE_MASK       0x3FFFFFFFu
#define VOXEL_PAGE_UINTS      128u
#define VOXEL_MAX_STEPS       512

struct VoxelChunk
{
    float4 originSize;     // xyz: world origin of the chunk, w: world size of one voxel
    uint4  boundsMinRes;   // xyz: min corner of non-air content (voxels), w: voxels per axis
    uint4  boundsMaxFlags; // xyz: max corner (exclusive), w: content flags
    int4   neighborsA;     // slots of the -X, +X, -Y, +Y neighbours (-1 = none)
    int4   neighborsB;     // x: -Z, y: +Z, z: level 0 brick table start, w: unused
    int4   lodBricks;      // x: level 1 brick table start, y: level 2 start, z: level count
};

StructuredBuffer<VoxelChunk> _VoxelChunks;
StructuredBuffer<uint>       _VoxelBricks;
StructuredBuffer<uint>       _VoxelPages;
StructuredBuffer<uint2>      _VoxelCells;     // per draw cell: packed content bounds (min, max) in chunk voxels
StructuredBuffer<uint>       _VoxelBlockInfo; // bits 0-7 visibility, 8-15 alpha mask index
StructuredBuffer<uint>       _VoxelDrawList;  // instance id -> draw cell id (slot * _VoxelMaxCells + cell)
StructuredBuffer<int4>       _VoxelChunkGrid; // toroidal chunk grid: x = slot (-1 empty), yzw = chunk coord

float  _VoxelMaxCells;          // draw cells per chunk slot
float4 _VoxelGridDims;          // xyz: grid size in chunks (powers of two)
float  _VoxelChunkWorldSize;    // world size of a chunk
float4 _VoxelLodDistances;      // x: camera distance where cells switch to level 1, y: to level 2 (0 = off)
float  _VoxelSunShadowDistance; // max length of voxel shadow rays (world units)
float  _VoxelShadowDetailDistance; // receivers closer than this trace full resolution shadow rays
float  _VoxelShadowMaxDistance;    // receivers farther than this get no voxel shadow rays

TEXTURE2D_ARRAY(_Textures);   SAMPLER(sampler_Textures);
TEXTURE2D_ARRAY(_AlphaMasks); SAMPLER(sampler_AlphaMasks);

// Look settings - globals pushed by VoxelRenderSettings (scene component).
float  _VoxelSmoothness;
float4 _VoxelTranslucentTint;
float4 _VoxelDeepColor;
float  _VoxelSurfaceOpacity;
float  _VoxelAbsorption;
float  _VoxelSpecular;
float4 _VoxelTexVariation;          // x: small scale, y: large scale (blocks per tile), z: large blend, w: mode (0 lerp, 1 multiply)
float  _VoxelShadowIgnoreDistance;  // occluders closer than this along a shadow ray are ignored (blocks)
float  _VoxelShadowMapBias;         // minimum push of voxel casters away from the light in URP shadow maps (blocks)

// Shaders with their own material cbuffer (e.g. the voxel shadow receiver) define this.
#ifndef VOXEL_NO_MATERIAL_CBUFFER
CBUFFER_START(UnityPerMaterial)
    float  _Cull;
    float  _ZTest;
CBUFFER_END
#endif

// --------------------------------------------------------------------------------------
// Data access
// --------------------------------------------------------------------------------------

uint VoxelBlockVisibility(uint id) { return _VoxelBlockInfo[id] & 0xFFu; }
uint VoxelBlockMask(uint id)       { return (_VoxelBlockInfo[id] >> 8) & 0xFFu; }

// Detail levels: 0 = full resolution, 1 = half, 2 = quarter. Each level has its own brick table.
uint VoxelLevelCount(VoxelChunk c) { return max((uint)c.lodBricks.z, 1u); }
int  VoxelLevelRes(VoxelChunk c, uint level) { return (int)(c.boundsMinRes.w >> level); }
float VoxelLevelScale(uint level) { return (float)(1u << level); }

uint VoxelFetchBrick(VoxelChunk c, uint level, int3 brick)
{
    uint bricksPerAxis = c.boundsMinRes.w >> (3u + level);
    uint start = level == 0u ? (uint)c.neighborsB.z : (level == 1u ? (uint)c.lodBricks.x : (uint)c.lodBricks.y);
    uint index = (uint)brick.x + ((uint)brick.y + (uint)brick.z * bricksPerAxis) * bricksPerAxis;
    return _VoxelBricks[start + index];
}

uint VoxelFetchInBrick(uint brickEntry, int3 local)
{
    if ((brickEntry & VOXEL_BRICK_UNIFORM) != 0u)
        return brickEntry & 0xFFu;

    uint li = (uint)local.x + (uint)local.y * 8u + (uint)local.z * 64u;
    uint word = _VoxelPages[(brickEntry & VOXEL_PAGE_MASK) * VOXEL_PAGE_UINTS + (li >> 2)];
    return (word >> ((li & 3u) * 8u)) & 0xFFu;
}

// Any voxel inside the chunk at the given level (outside the content box bricks are uniform air).
uint VoxelFetch(VoxelChunk c, uint level, int3 p)
{
    return VoxelFetchInBrick(VoxelFetchBrick(c, level, p >> 3), p & 7);
}

bool VoxelInChunk(VoxelChunk c, uint level, int3 p)
{
    int res = VoxelLevelRes(c, level);
    return all(p >= 0) && all(p < res);
}

// Reads a voxel just outside the chunk from the neighbouring chunk (same level when it has it).
uint VoxelFetchNeighbor(VoxelChunk c, uint level, int3 p)
{
    int res = VoxelLevelRes(c, level);
    int n = -1;
    if (p.x < 0)         n = c.neighborsA.x;
    else if (p.x >= res) n = c.neighborsA.y;
    else if (p.y < 0)    n = c.neighborsA.z;
    else if (p.y >= res) n = c.neighborsA.w;
    else if (p.z < 0)    n = c.neighborsB.x;
    else if (p.z >= res) n = c.neighborsB.y;

    if (n < 0)
        return 0u;

    VoxelChunk nc = _VoxelChunks[n];
    uint nLevel = min(level, VoxelLevelCount(nc) - 1u);
    float3 world = c.originSize.xyz + ((float3)p + 0.5) * (c.originSize.w * VoxelLevelScale(level));
    int3 q = (int3)floor((world - nc.originSize.xyz) / (nc.originSize.w * VoxelLevelScale(nLevel)));
    q = clamp(q, 0, VoxelLevelRes(nc, nLevel) - 1);
    return VoxelFetch(nc, nLevel, q);
}

// Voxel at p, which may lie in this chunk or just outside it.
uint VoxelFetchAny(VoxelChunk c, uint level, int3 p)
{
    return VoxelInChunk(c, level, p) ? VoxelFetch(c, level, p) : VoxelFetchNeighbor(c, level, p);
}

// --------------------------------------------------------------------------------------
// Surface helpers
// --------------------------------------------------------------------------------------

float2 VoxelFaceUV(int axis, float3 p)
{
    float2 uv = axis == 0 ? p.zy : (axis == 1 ? p.xz : p.xy);
    return frac(uv);
}

bool VoxelMaskPass(uint id, int axis, float3 p)
{
    uint mask = VoxelBlockMask(id);
    if (mask == 0u)
        return true;

    // Slice index == BlockDatabase alphaMaskIndex (0 = opaque, 1 = LeafAlpha, 2 = LeafAlpha2)
    float2 uv = VoxelFaceUV(axis, p);
    return SAMPLE_TEXTURE2D_ARRAY_LOD(_AlphaMasks, sampler_AlphaMasks, uv, mask, 0).r > 0.5;
}

int3 VoxelAxisMask(int axis)
{
    return int3(axis == 0, axis == 1, axis == 2);
}

// Normal of the face a ray crossed on 'axis', facing back towards the ray origin.
float3 VoxelFaceNormal(int axis, float3 dir)
{
    float3 m = (float3)VoxelAxisMask(axis);
    return -sign(dir) * m;
}

int VoxelMajorAxis(float3 d)
{
    float3 a = abs(d);
    return (a.x >= a.y && a.x >= a.z) ? 0 : (a.y >= a.z ? 1 : 2);
}

float3 VoxelSafeDir(float3 d)
{
    const float eps = 1e-6;
    return float3(abs(d.x) < eps ? eps : d.x,
                  abs(d.y) < eps ? eps : d.y,
                  abs(d.z) < eps ? eps : d.z);
}

// Draw cell bounds are packed as 8 bits per component: x = min, y = max (exclusive).
void VoxelUnpackBounds(uint2 packed, out float3 bmin, out float3 bmax)
{
    bmin = float3(packed.x & 0xFFu, (packed.x >> 8) & 0xFFu, (packed.x >> 16) & 0xFFu);
    bmax = float3(packed.y & 0xFFu, (packed.y >> 8) & 0xFFu, (packed.y >> 16) & 0xFFu);
}

void VoxelChunkBounds(VoxelChunk c, out float3 bmin, out float3 bmax)
{
    bmin = (float3)c.boundsMinRes.xyz;
    bmax = (float3)c.boundsMaxFlags.xyz;
}

// --------------------------------------------------------------------------------------
// Traversal
// --------------------------------------------------------------------------------------

struct VoxelCursor
{
    float t;    // ray parameter at the entry of the current cell (voxel units)
    int3  p;    // current voxel
    int   axis; // axis of the face we entered through (-1 = ray started inside)
};

struct VoxelHit
{
    float  t;
    uint   id;
    int    axis;
    float3 posVoxel;
};

bool VoxelRayBox(float3 o, float3 invD, float3 bmin, float3 bmax,
                 out float tNear, out float tFar, out int nearAxis)
{
    float3 t0 = (bmin - o) * invD;
    float3 t1 = (bmax - o) * invD;
    float3 tmin = min(t0, t1);
    float3 tmax = max(t0, t1);
    tNear = max(max(tmin.x, tmin.y), tmin.z);
    tFar  = min(min(tmax.x, tmax.y), tmax.z);
    nearAxis = (tmin.x >= tmin.y && tmin.x >= tmin.z) ? 0 : (tmin.y >= tmin.z ? 1 : 2);
    return tFar >= tNear;
}

bool VoxelBeginRay(float3 bmin, float3 bmax, float3 o, float3 d, float3 invD, float tMin, out VoxelCursor cur)
{
    float tNear, tFar; int nearAxis;
    cur = (VoxelCursor)0;
    if (!VoxelRayBox(o, invD, bmin, bmax, tNear, tFar, nearAxis) || tFar < tMin)
        return false;

    cur.t = max(tNear, tMin);
    cur.axis = tNear >= tMin ? nearAxis : -1;
    cur.p = clamp((int3)floor(o + d * cur.t), (int3)bmin, (int3)bmax - 1);
    return true;
}

// Moves the cursor across the exit face of the cell [cellMin, cellMax).
void VoxelAdvance(inout VoxelCursor cur, float3 o, float3 d, float3 invD, int3 stepDir,
                  float3 cellMin, float3 cellMax)
{
    float3 positive = (float3)(stepDir > 0);
    float3 tExit3 = (lerp(cellMin, cellMax, positive) - o) * invD;
    float tExit = min(tExit3.x, min(tExit3.y, tExit3.z));
    int axis = (tExit3.x <= tExit3.y && tExit3.x <= tExit3.z) ? 0 : (tExit3.y <= tExit3.z ? 1 : 2);

    int3 np = clamp((int3)floor(o + d * tExit), (int3)cellMin, (int3)cellMax - 1);
    int3 across = (int3)lerp(cellMin - 1.0, cellMax, positive);
    int3 m = VoxelAxisMask(axis);
    np = np * (1 - m) + across * m;

    cur.t = tExit;
    cur.p = np;
    cur.axis = axis;
}

// Extent of the step containing p: the whole brick when it is uniform, else one voxel,
// clipped to the traversal bounds.
void VoxelStepBounds(int3 p, bool wholeBrick, float3 bmin, float3 bmax, out float3 cellMin, out float3 cellMax)
{
    cellMin = wholeBrick ? (float3)((p >> 3) * 8) : (float3)p;
    cellMax = cellMin + (wholeBrick ? 8.0 : 1.0);
    cellMin = max(cellMin, bmin);
    cellMax = min(cellMax, bmax);
}

bool VoxelOutside(int3 p, float3 bmin, float3 bmax)
{
    return any(p < (int3)bmin) || any(p >= (int3)bmax);
}

// First opaque (or alpha-mask passing) surface along the ray inside [bmin, bmax).
// o/d are in chunk voxel space.
// skipStartSolid: when the ray starts inside solid voxels, ignore them until it reaches open
// space (used by shadow rays that skip nearby occluders).
bool VoxelMarchOpaque(VoxelChunk c, uint level, float3 bmin, float3 bmax, float3 o, float3 d, float tMin, out VoxelHit hit, bool skipStartSolid = false)
{
    hit = (VoxelHit)0;
    d = VoxelSafeDir(d);
    float3 invD = 1.0 / d;

    VoxelCursor cur;
    if (!VoxelBeginRay(bmin, bmax, o, d, invD, tMin, cur))
        return false;

    bool skipping = skipStartSolid && cur.axis < 0;

    int3 stepDir = int3(d.x > 0 ? 1 : -1, d.y > 0 ? 1 : -1, d.z > 0 ? 1 : -1);
    int majorAxis = VoxelMajorAxis(d);
    uint prevId = 0u;
    uint prevVis = VOXEL_VIS_INVISIBLE;

    [loop]
    for (int i = 0; i < VOXEL_MAX_STEPS; i++)
    {
        uint entry = VoxelFetchBrick(c, level, cur.p >> 3);
        bool isUniform = (entry & VOXEL_BRICK_UNIFORM) != 0u;
        uint id = isUniform ? (entry & 0xFFu) : VoxelFetchInBrick(entry, cur.p & 7);
        uint vis = VoxelBlockVisibility(id);

        int faceAxis = cur.axis >= 0 ? cur.axis : majorAxis;
        float3 pos = o + d * cur.t;

        if (skipping && (vis == VOXEL_VIS_OPAQUE || vis == VOXEL_VIS_STACKED))
        {
            float3 sMin, sMax;
            VoxelStepBounds(cur.p, isUniform, bmin, bmax, sMin, sMax);
            VoxelAdvance(cur, o, d, invD, stepDir, sMin, sMax);
            if (VoxelOutside(cur.p, bmin, bmax))
                return false;
            continue;
        }
        skipping = false;

        if (vis == VOXEL_VIS_OPAQUE || (vis == VOXEL_VIS_STACKED && VoxelMaskPass(id, faceAxis, pos)))
        {
            hit.t = cur.t; hit.id = id; hit.axis = faceAxis; hit.posVoxel = pos;
            return true;
        }

        // Far side of a masked block (they render double sided) - only towards open space.
        if (prevVis == VOXEL_VIS_STACKED && (vis == VOXEL_VIS_INVISIBLE || vis == VOXEL_VIS_TRANSLUCENT) && VoxelMaskPass(prevId, faceAxis, pos))
        {
            hit.t = cur.t; hit.id = prevId; hit.axis = faceAxis; hit.posVoxel = pos;
            return true;
        }

        float3 cellMin, cellMax;
        VoxelStepBounds(cur.p, isUniform && vis != VOXEL_VIS_STACKED, bmin, bmax, cellMin, cellMax);
        VoxelAdvance(cur, o, d, invD, stepDir, cellMin, cellMax);
        prevId = id;
        prevVis = vis;

        if (VoxelOutside(cur.p, bmin, bmax))
        {
            // Leaving through a masked block: its far face is visible only when the next voxel is
            // open space. Next to a solid or masked block that block draws the shared face, and
            // drawing both at the same depth would z-fight.
            if (vis == VOXEL_VIS_STACKED)
            {
                uint nextVis = VoxelBlockVisibility(VoxelFetchAny(c, level, cur.p));
                pos = o + d * cur.t;
                if ((nextVis == VOXEL_VIS_INVISIBLE || nextVis == VOXEL_VIS_TRANSLUCENT) && VoxelMaskPass(id, cur.axis, pos))
                {
                    hit.t = cur.t; hit.id = id; hit.axis = cur.axis; hit.posVoxel = pos;
                    return true;
                }
            }
            return false;
        }
    }
    return false;
}

// Low detail occlusion test that only walks bricks (8x8x8). A brick blocks the ray when it is
// a uniform opaque / masked brick or a mixed brick flagged as mostly solid.
bool VoxelMarchCoarse(VoxelChunk c, float3 o, float3 d, float tMin)
{
    d = VoxelSafeDir(d);
    float3 invD = 1.0 / d;

    float3 bmin, bmax;
    VoxelChunkBounds(c, bmin, bmax);
    float tNear, tFar; int nearAxis;
    if (!VoxelRayBox(o, invD, bmin, bmax, tNear, tFar, nearAxis) || tFar < tMin)
        return false;

    // Brick space
    float3 ob = o / 8.0;
    float3 bbMin = floor(bmin / 8.0);
    float3 bbMax = ceil(bmax / 8.0);
    float t = max(tNear, tMin) / 8.0;
    int3 b = clamp((int3)floor(ob + d * t), (int3)bbMin, (int3)bbMax - 1);
    int3 stepDir = int3(d.x > 0 ? 1 : -1, d.y > 0 ? 1 : -1, d.z > 0 ? 1 : -1);
    float3 positive = (float3)(stepDir > 0);
    bool skipping = tNear < tMin;   // started inside the content: ignore the bricks we start in

    [loop]
    for (int i = 0; i < 64; i++)
    {
        uint entry = VoxelFetchBrick(c, 0u, b);
        uint uvis = VoxelBlockVisibility(entry & 0xFFu);
        bool blocks = (entry & VOXEL_BRICK_UNIFORM) != 0u
            ? (uvis == VOXEL_VIS_OPAQUE || uvis == VOXEL_VIS_STACKED)
            : (entry & VOXEL_BRICK_DENSE) != 0u;
        if (blocks && !skipping)
            return true;
        if (!blocks)
            skipping = false;

        float3 tExit3 = ((float3)b + positive - ob) * invD;
        int axis = (tExit3.x <= tExit3.y && tExit3.x <= tExit3.z) ? 0 : (tExit3.y <= tExit3.z ? 1 : 2);
        b += stepDir * VoxelAxisMask(axis);
        if (any(b < (int3)bbMin) || any(b >= (int3)bbMax))
            return false;
    }
    return false;
}

// --------------------------------------------------------------------------------------
// World space queries (across chunks)
// --------------------------------------------------------------------------------------

// Slot of the chunk at a chunk coordinate, or -1 when it is not loaded / empty.
int VoxelLookupChunk(int3 coord)
{
    int3 dims = (int3)_VoxelGridDims.xyz;
    int3 w = coord & (dims - 1);
    int4 e = _VoxelChunkGrid[w.x + (w.y + w.z * dims.y) * dims.x];
    return (e.x >= 0 && all(e.yzw == coord)) ? e.x : -1;
}

// 1 when nothing blocks the ray from posWS along dirWS (towards the sun), 0 otherwise.
// Walks the chunk grid and marches every loaded chunk it crosses. 'coarse' only tests bricks.
float VoxelSunShadowRay(float3 posWS, float3 dirWS, bool coarse, float ignoreDistance)
{
    float cs = _VoxelChunkWorldSize;
    if (cs <= 0.0)
        return 1.0;

    float3 d = VoxelSafeDir(dirWS);
    float3 invD = 1.0 / d;
    float3 p = posWS / cs;
    int3 chunk = (int3)floor(p);
    int3 stepDir = int3(d.x > 0 ? 1 : -1, d.y > 0 ? 1 : -1, d.z > 0 ? 1 : -1);
    float3 positive = (float3)(stepDir > 0);
    float maxT = _VoxelSunShadowDistance / cs;
    float t = 0.0;

    [loop]
    for (int i = 0; i < 32 && t < maxT; i++)
    {
        int slot = VoxelLookupChunk(chunk);
        if (slot >= 0)
        {
            VoxelChunk c = _VoxelChunks[slot];
            float3 o = (posWS - c.originSize.xyz) / c.originSize.w;
            // Occluders right next to the receiver are ignored: on gentle voxel slopes every
            // 1-block step would otherwise shadow the next one and draw stripes.
            float ignore = max(ignoreDistance, 0.0) / c.originSize.w;
            if (coarse)
            {
                // Also skip the receiver's own brick so a surface does not shadow itself.
                if (VoxelMarchCoarse(c, o, d, max(8.0, ignore)))
                    return 0.0;
            }
            else
            {
                float3 bmin, bmax;
                VoxelChunkBounds(c, bmin, bmax);
                VoxelHit hit;
                if (VoxelMarchOpaque(c, 0u, bmin, bmax, o, d, ignore, hit, true) && hit.t * c.originSize.w < _VoxelSunShadowDistance)
                    return 0.0;
            }
        }

        float3 tExit3 = ((float3)chunk + positive - p) * invD;
        int axis = (tExit3.x <= tExit3.y && tExit3.x <= tExit3.z) ? 0 : (tExit3.y <= tExit3.z ? 1 : 2);
        t = min(tExit3.x, min(tExit3.y, tExit3.z));
        chunk += stepDir * VoxelAxisMask(axis);
    }
    return 1.0;
}

// Sun shadow with distance based detail: full resolution near the camera, brick resolution
// further out, none beyond _VoxelShadowMaxDistance.
float VoxelSunShadow(float3 posWS, float3 normalWS, float3 sunDirWS)
{
    if (dot(normalWS, sunDirWS) <= 0.0)
        return 1.0;

    float dist = distance(posWS, GetCameraPositionWS());
    if (dist > _VoxelShadowMaxDistance)
        return 1.0;

    return VoxelSunShadowRay(posWS + normalWS * 0.01, sunDirWS, dist > _VoxelShadowDetailDistance, _VoxelShadowIgnoreDistance);
}

// Voxel shadow for non-voxel surfaces (player, decorations): no ignore distance, so walls and
// trees right next to the object still shadow it. 1 when the chunk data is not available.
float VoxelSunShadowForObject(float3 posWS, float3 normalWS, float3 sunDirWS)
{
    if (_VoxelChunkWorldSize <= 0.0 || dot(normalWS, sunDirWS) <= 0.0)
        return 1.0;

    float dist = distance(posWS, GetCameraPositionWS());
    if (dist > _VoxelShadowMaxDistance)
        return 1.0;

    return VoxelSunShadowRay(posWS + normalWS * 0.02, sunDirWS, dist > _VoxelShadowDetailDistance, 0.0);
}

// --------------------------------------------------------------------------------------
// Box vertex generation
// --------------------------------------------------------------------------------------

static const uint2 kVoxelQuadPos[6] = { uint2(0,0), uint2(1,0), uint2(1,1), uint2(0,0), uint2(1,1), uint2(0,1) };
static const uint2 kVoxelQuadNeg[6] = { uint2(0,0), uint2(1,1), uint2(1,0), uint2(0,0), uint2(0,1), uint2(1,1) };

// Content bounds of a draw cell at a detail level (rounded outwards to that level's voxels).
void VoxelLevelBounds(uint2 packedBounds, uint level, out float3 bmin, out float3 bmax)
{
    VoxelUnpackBounds(packedBounds, bmin, bmax);
    float s = VoxelLevelScale(level);
    bmin = floor(bmin / s);
    bmax = ceil(bmax / s);
}

// Detail level for a draw cell, from its distance to the camera. Picked per cell (not per
// pixel) so a cell's box and its ray march always agree.
uint VoxelSelectLevel(VoxelChunk c, uint2 packedBounds)
{
    if (_VoxelLodDistances.x <= 0.0)
        return 0u;

    float3 bmin, bmax;
    VoxelUnpackBounds(packedBounds, bmin, bmax);
    float3 centerWS = c.originSize.xyz + (bmin + bmax) * 0.5 * c.originSize.w;
    float dist = distance(centerWS, GetCameraPositionWS());

    uint level = dist > _VoxelLodDistances.y ? 2u : (dist > _VoxelLodDistances.x ? 1u : 0u);
    return min(level, VoxelLevelCount(c) - 1u);
}

// Emits one corner of a draw cell's content box. 36 vertices = 6 faces * 2 triangles.
void VoxelBoxVertex(uint vertexID, uint instanceID, out uint slot, out uint2 cellBounds, out uint level,
                    out float3 positionWS, out float3 faceNormalWS)
{
    uint cellId = _VoxelDrawList[instanceID];
    slot = cellId / (uint)_VoxelMaxCells;
    cellBounds = _VoxelCells[cellId];
    VoxelChunk c = _VoxelChunks[slot];
    level = VoxelSelectLevel(c, cellBounds);

    uint face = vertexID / 6u;
    uint k = vertexID - face * 6u;
    uint axis = face >> 1;
    bool positive = (face & 1u) != 0u;
    uint2 q = positive ? kVoxelQuadPos[k] : kVoxelQuadNeg[k];
    float a = positive ? 1.0 : 0.0;

    float3 corner;
    if (axis == 0u)      corner = float3(a, q.x, q.y);
    else if (axis == 1u) corner = float3(q.y, a, q.x);
    else                 corner = float3(q.x, q.y, a);

    float3 bmin, bmax;
    VoxelLevelBounds(cellBounds, level, bmin, bmax);
    positionWS = c.originSize.xyz + lerp(bmin, bmax, corner) * (c.originSize.w * VoxelLevelScale(level));
    faceNormalWS = (float3)VoxelAxisMask((int)axis) * (positive ? 1.0 : -1.0);
}

// Camera ray through a world position (handles orthographic cameras).
void VoxelCameraRay(float3 positionWS, out float3 ro, out float3 rd, out float tMinWS)
{
    if (unity_OrthoParams.w > 0.5)
    {
        rd = normalize(-UNITY_MATRIX_V[2].xyz);
        ro = positionWS - rd * _ProjectionParams.z;
        tMinWS = 0.0;
    }
    else
    {
        ro = GetCameraPositionWS();
        rd = normalize(positionWS - ro);
        tMinWS = _ProjectionParams.y;
    }
}

// World space face coordinates (in blocks) so textures line up across chunks and LODs.
float2 VoxelWorldFaceCoords(int axis, float3 posWS)
{
    return axis == 0 ? posWS.zy : (axis == 1 ? posWS.xz : posWS.xy);
}

// Block texture with large scale variation: the same texture sampled at a small and a large
// tiling and blended, which breaks up repetition across big areas.
float4 VoxelSampleBlockTexture(uint id, int axis, float3 posWS)
{
    float2 p = VoxelWorldFaceCoords(axis, posWS);
    float smallScale = max(_VoxelTexVariation.x, 0.01);
    float largeScale = max(_VoxelTexVariation.y, 0.01);
    float blend = saturate(_VoxelTexVariation.z);

    float4 small = SAMPLE_TEXTURE2D_ARRAY_LOD(_Textures, sampler_Textures, frac(p / smallScale), id, 0);
    if (blend <= 0.0)
        return small;
    float4 large = SAMPLE_TEXTURE2D_ARRAY_LOD(_Textures, sampler_Textures, frac(p / largeScale), id, 0);

    if (_VoxelTexVariation.w > 0.5)
        return float4(small.rgb * lerp(1.0, large.rgb * 2.0, blend), small.a);   // multiply (overlay-like)
    return lerp(small, large, blend);
}

float4 VoxelSampleAlbedo(VoxelHit hit, float3 hitWS)
{
    return VoxelSampleBlockTexture(hit.id, hit.axis, hitWS);
}

float VoxelClipDepth(float3 positionWS)
{
    float4 cs = TransformWorldToHClip(positionWS);
    return cs.z / cs.w;
}

// Depth output semantic. Outside a box the hit is never in front of the rasterized front
// face, so we can promise that and keep early depth testing alive.
#if defined(_VOXEL_INSIDE)
    #define VOXEL_DEPTH_SEMANTIC SV_Depth
#elif UNITY_REVERSED_Z
    #define VOXEL_DEPTH_SEMANTIC SV_DepthLessEqual
#else
    #define VOXEL_DEPTH_SEMANTIC SV_DepthGreaterEqual
#endif

// Keeps an output depth on the far side of the rasterized depth, as promised by the
// conservative depth semantics (guards against small precision / bias differences).
float VoxelConservativeDepth(float depth, float rasterDepth)
{
#if defined(_VOXEL_INSIDE)
    return depth;
#elif UNITY_REVERSED_Z
    return min(depth, rasterDepth);
#else
    return max(depth, rasterDepth);
#endif
}

struct VoxelAttributes
{
    uint vertexID   : SV_VertexID;
    uint instanceID : SV_InstanceID;
};

struct VoxelVaryings
{
    // D3D11 requires this interpolation mode for SV_POSITION when a pixel shader
    // outputs conservative depth (SV_DepthLessEqual / SV_DepthGreaterEqual).
    noperspective centroid float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    nointerpolation uint slot : TEXCOORD1;
    nointerpolation uint2 cellBounds : TEXCOORD2;
    nointerpolation uint level : TEXCOORD3;
};

VoxelVaryings VoxelVert(VoxelAttributes input)
{
    VoxelVaryings o;
    float3 n;
    VoxelBoxVertex(input.vertexID, input.instanceID, o.slot, o.cellBounds, o.level, o.positionWS, n);
    o.positionCS = TransformWorldToHClip(o.positionWS);
    return o;
}

// Marches a camera ray through the draw cell of this fragment. Returns false when nothing was hit.
bool VoxelTraceCamera(VoxelVaryings input, out VoxelHit hit, out float3 hitWS, out float3 normalWS, out float3 rayDirWS)
{
    VoxelChunk c = _VoxelChunks[input.slot];
    float3 ro; float tMinWS;
    VoxelCameraRay(input.positionWS, ro, rayDirWS, tMinWS);

    float3 bmin, bmax;
    VoxelLevelBounds(input.cellBounds, input.level, bmin, bmax);

    float vs = c.originSize.w * VoxelLevelScale(input.level);
    float3 o = (ro - c.originSize.xyz) / vs;
    bool found = VoxelMarchOpaque(c, input.level, bmin, bmax, o, rayDirWS, tMinWS / vs, hit);

    hitWS = ro + rayDirWS * (hit.t * vs);
    normalWS = VoxelFaceNormal(hit.axis, rayDirWS);
    return found;
}

#endif

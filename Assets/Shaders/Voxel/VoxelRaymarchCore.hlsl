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

// Surface decorations (_VoxelBlockInfo bits 16-23, BlockDatabase surfaceDecoration).
#define VOXEL_DECOR_GRASS     1u
// Id the pack job writes into the air voxel above a grass top (VoxelGpuFormat.GrassMarker).
// Invisible like air; near the camera the march tests its grass blades (VoxelGrass.hlsl).
#define VOXEL_ID_GRASS        255u

struct VoxelChunk
{
    float4 originSize;     // xyz: world origin of the chunk, w: world size of one voxel
    uint4  boundsMinRes;   // xyz: min corner of non-air content (voxels), w: voxels per axis
    uint4  boundsMaxFlags; // xyz: max corner (exclusive), w: content flags
    int4   neighborsA;     // slots of the -X, +X, -Y, +Y neighbours (-1 = none)
    int4   neighborsB;     // x: -Z, y: +Z, z: level 0 brick table start, w: level 3 start
    int4   lodBricks;      // x: level 1 brick table start, y: level 2 start, z: level count,
                           // w: finest level with voxel pages (distant chunks skip the fine levels)
};

StructuredBuffer<VoxelChunk> _VoxelChunks;
StructuredBuffer<uint>       _VoxelBricks;
StructuredBuffer<uint>       _VoxelPages;
StructuredBuffer<uint2>      _VoxelCells;     // per draw cell: packed content bounds (min, max) in chunk voxels
StructuredBuffer<uint>       _VoxelBlockInfo; // bits 0-7 visibility, 8-15 alpha mask index, 16-23 surface decoration, 24 palette surface
StructuredBuffer<float4>     _VoxelSurfaceParams; // per block: patch = saturate(colour * x + y), texel position = lerp(patch, colour, z)
StructuredBuffer<uint>       _VoxelDrawList;  // instance id -> draw cell id (slot * _VoxelMaxCells + cell)
StructuredBuffer<int4>       _VoxelChunkGrid; // toroidal chunk grid: x = slot (-1 empty), yzw = chunk coord

float  _VoxelMaxCells;          // draw cells per chunk slot
float4 _VoxelGridDims;          // xyz: grid size in chunks (powers of two)
float  _VoxelChunkWorldSize;    // world size of a chunk
float4 _VoxelLodDistances;      // camera distance where cells switch to level 1 (x), 2 (y), 3 (z, 0 = never); x 0 = LOD off
float  _VoxelSunShadowDistance; // max length of voxel shadow rays (world units)
float  _VoxelShadowDetailDistance; // receivers closer than this trace full resolution shadow rays
float  _VoxelShadowMaxDistance;    // receivers farther than this get no voxel shadow rays
float  _VoxelFarShadowDetail;      // ray detail past _VoxelShadowDetailDistance: 1 half, 2 quarter resolution, 3 bricks

TEXTURE2D_ARRAY(_Textures);   SAMPLER(sampler_Textures);
TEXTURE2D_ARRAY(_AlphaMasks); SAMPLER(sampler_AlphaMasks);
TEXTURE2D_ARRAY(_VoxelSurfaceLut); SAMPLER(sampler_VoxelSurfaceLut);   // BlockTextureArrayBuilder.generatedSurfaceLut
float  _VoxelSurfaceLutSize;

// Look settings - globals pushed by VoxelRenderSettings (scene component).
float  _VoxelSmoothness;
float4 _VoxelTranslucentTint;
float4 _VoxelDeepColor;
float  _VoxelSurfaceOpacity;
float  _VoxelAbsorption;
float  _VoxelSpecular;
float4 _VoxelTexVariation;          // x: small scale, y: large scale (blocks per tile), z: large blend, w: mode (0 lerp, 1 multiply)
float  _VoxelShadowIgnoreDistance;  // occluders closer than this along a shadow ray are ignored (blocks)
float  _VoxelSolidLeavesFromLevel;  // detail level from which masked blocks (leaves) render solid; 0 = never
float  _VoxelTexelCount;            // texels per block texture edge

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
uint VoxelBlockDecoration(uint id) { return (_VoxelBlockInfo[id] >> 16) & 0xFFu; }

// Detail levels: 0 = full resolution, 1 = half, 2 = quarter, 3 = eighth. Each level has its own
// brick table. Levels below VoxelMinLevel have brick tables (coarse shadows read their flags)
// but no voxel pages, so voxel reads always go through VoxelClampLevel.
uint VoxelLevelCount(VoxelChunk c) { return max((uint)c.lodBricks.z, 1u); }
uint VoxelMinLevel(VoxelChunk c) { return min((uint)c.lodBricks.w, VoxelLevelCount(c) - 1u); }
uint VoxelClampLevel(VoxelChunk c, uint level) { return clamp(level, VoxelMinLevel(c), VoxelLevelCount(c) - 1u); }
int  VoxelLevelRes(VoxelChunk c, uint level) { return (int)(c.boundsMinRes.w >> level); }
float VoxelLevelScale(uint level) { return (float)(1u << level); }

uint VoxelFetchBrick(VoxelChunk c, uint level, int3 brick)
{
    uint bricksPerAxis = c.boundsMinRes.w >> (3u + level);
    uint start = level == 0u ? (uint)c.neighborsB.z : (level == 1u ? (uint)c.lodBricks.x
               : (level == 2u ? (uint)c.lodBricks.y : (uint)c.neighborsB.w));
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
    uint nLevel = VoxelClampLevel(nc, level);
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

// Far horizon chunks (VoxelWorldRenderer.SetHorizonChunk): coarse terrain beyond the loaded
// chunks. Their voxels are hidden where the ring inside them has terrain: ring 0 under any loaded
// chunk column, ring n inside ring n - 1's square.
#define VOXEL_FLAG_HORIZON 4u
StructuredBuffer<int4> _VoxelColumnGrid;   // toroidal x/z grid: x = loaded chunks in the column, yz = column coord
float4 _VoxelHorizonInner[4];             // per ring: min x, min z, max x, max z of the ring inside it

bool VoxelHorizonHidden(VoxelChunk c, int3 p)
{
    float3 world = c.originSize.xyz + ((float3)p + 0.5) * c.originSize.w;
    uint ring = (c.boundsMaxFlags.w >> 8) & 0xFu;
    if (ring == 0u)
    {
        int2 col = (int2)floor(world.xz / _VoxelChunkWorldSize);
        int2 dims = (int2)_VoxelGridDims.xz;
        int4 e = _VoxelColumnGrid[(col.x & (dims.x - 1)) + (col.y & (dims.y - 1)) * dims.x];
        return e.x > 0 && e.y == col.x && e.z == col.y;
    }
    float4 sq = _VoxelHorizonInner[ring];
    return world.x >= sq.x && world.z >= sq.y && world.x < sq.z && world.z < sq.w;
}

// Voxel the camera march sees at p inside a draw cell. A cell with grass in the chunk's top layer
// reaches one voxel above the chunk (the blades stand in the chunk above, whose data does not
// mark them): that layer is the grass marker over exposed grass, air otherwise.
uint VoxelFetchMarch(VoxelChunk c, uint level, int3 p, out bool isUniform)
{
    isUniform = false;
    uint id = 0u;
    if (p.y >= VoxelLevelRes(c, level))
    {
        if (level == 0u && VoxelBlockDecoration(VoxelFetch(c, 0u, p - int3(0, 1, 0))) == VOXEL_DECOR_GRASS
            && VoxelBlockVisibility(VoxelFetchNeighbor(c, 0u, p)) == VOXEL_VIS_INVISIBLE)
            id = VOXEL_ID_GRASS;
    }
    else
    {
        uint entry = VoxelFetchBrick(c, level, p >> 3);
        isUniform = (entry & VOXEL_BRICK_UNIFORM) != 0u;
        id = isUniform ? (entry & 0xFFu) : VoxelFetchInBrick(entry, p & 7);
    }
    if (id != 0u && (c.boundsMaxFlags.w & VOXEL_FLAG_HORIZON) != 0u && VoxelHorizonHidden(c, p))
    {
        id = 0u;
        isUniform = false;
    }
    return id;
}

// Whether the level 0 voxel v (id already fetched; v may lie just outside the chunk) is the air
// above a grass top. Inside a chunk that is the grass marker; only air in the bottom layer (grass
// in the chunk below) or just above the chunk (grass in the top layer) needs the block below.
bool VoxelGrassAir(VoxelChunk c, int3 v, uint id)
{
    if (id == VOXEL_ID_GRASS)
        return true;
    if (id != 0u || (v.y != 0 && v.y != VoxelLevelRes(c, 0u)))
        return false;
    return VoxelBlockDecoration(VoxelFetchAny(c, 0u, v - int3(0, 1, 0))) == VOXEL_DECOR_GRASS;
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
    // Set by the texel relief (VoxelRelief.hlsl) when it refined the hit.
    uint   refined;
    int    texAxis;    // face whose texture colours the hit column
    float3 texPosWS;   // texel centre of the hit column
    float  occlusion;  // ambient occlusion from the column depth (1 = none)
    uint   grass;      // 1 = a grass blade (id is the grass block it grows from)
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

#include "VoxelGrass.hlsl"

// First opaque (or alpha-mask passing) surface along the ray inside [bmin, bmax).
// o/d are in chunk voxel space.
// skipStartSolid: when the ray starts inside solid voxels, ignore them until it reaches open
// space (used by shadow rays that skip nearby occluders).
// stopAtMaskedBefore: masked voxels entered before this t are returned as hits at their entry
// (the texel relief gives them thickness). 0 = off.
// stopAtGrassBefore: the stretch of the ray in the grass layer (air above grass tops, level 0,
// below the blade tips) before this t is returned in grassSpan (x: first entry, y: last exit;
// -1 = none) for VoxelGrassSegment. The blades are not tested here: inside this loop every pixel
// of a GPU wave would wait for each one that stops at grass.
bool VoxelMarchVoxels(VoxelChunk c, uint level, float3 bmin, float3 bmax, float3 o, float3 d, float tMin, out VoxelHit hit,
                      bool skipStartSolid, float stopAtMaskedBefore, float stopAtGrassBefore, inout float2 grassSpan)
{
    hit = (VoxelHit)0;
    d = VoxelSafeDir(d);
    float3 invD = 1.0 / d;

    VoxelCursor cur;
    if (!VoxelBeginRay(bmin, bmax, o, d, invD, tMin, cur))
        return false;

    bool skipping = skipStartSolid && cur.axis < 0;

    // Far away the alpha mask holes are smaller than a pixel: treat masked blocks as solid so
    // rays stop at the first leaf instead of marching through the canopy.
    bool solidMasked = _VoxelSolidLeavesFromLevel > 0.5 && level >= (uint)_VoxelSolidLeavesFromLevel;

    int3 stepDir = int3(d.x > 0 ? 1 : -1, d.y > 0 ? 1 : -1, d.z > 0 ? 1 : -1);
    int majorAxis = VoxelMajorAxis(d);
    uint prevId = 0u;
    uint prevVis = VOXEL_VIS_INVISIBLE;
    float grassTips = VoxelGrassTips();   // blade tips above the grass top (voxels = blocks)

    [loop]
    for (int i = 0; i < VOXEL_MAX_STEPS; i++)
    {
        bool isUniform = false;
        uint id = VoxelFetchMarch(c, level, cur.p, isUniform);
        uint vis = VoxelBlockVisibility(id);
        if (solidMasked && vis == VOXEL_VIS_STACKED)
            vis = VOXEL_VIS_OPAQUE;

        int faceAxis = cur.axis >= 0 ? cur.axis : majorAxis;
        float3 pos = o + d * cur.t;

        if (skipping && vis == VOXEL_VIS_OPAQUE)
        {
            float3 sMin, sMax;
            VoxelStepBounds(cur.p, isUniform, bmin, bmax, sMin, sMax);
            VoxelAdvance(cur, o, d, invD, stepDir, sMin, sMax);
            if (VoxelOutside(cur.p, bmin, bmax))
                return false;
            continue;
        }
        // A shadow ray starting inside a masked block (a surface under or inside leaves) does not
        // skip it, or it would ignore the whole canopy above. It has no entry face to test
        // either, so only the block's far face (and everything behind it) can block the ray.
        bool startsInMasked = skipping && vis == VOXEL_VIS_STACKED && cur.axis < 0;
        skipping = false;

        if (vis == VOXEL_VIS_STACKED && cur.t < stopAtMaskedBefore)
        {
            hit.t = cur.t; hit.id = id; hit.axis = faceAxis; hit.posVoxel = pos;
            return true;
        }

        if (vis == VOXEL_VIS_OPAQUE || (vis == VOXEL_VIS_STACKED && !startsInMasked && VoxelMaskPass(id, faceAxis, pos)))
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

        // Air above grass where the ray dips below the blade tips: part of the grass stretch.
        if (id == VOXEL_ID_GRASS && cur.t < stopAtGrassBefore)
        {
            float3 tExit3 = ((float3)cur.p + (float3)(stepDir > 0) - o) * invD;
            float tExit = min(tExit3.x, min(tExit3.y, tExit3.z));
            if (min(pos.y, o.y + d.y * tExit) - (float)cur.p.y < grassTips)
            {
                if (grassSpan.x < 0.0)
                    grassSpan.x = cur.t;
                grassSpan.y = tExit;
            }
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

#define VOXEL_GRASS_SEGMENT_VOXELS 32

// Grass blades along [tFrom, tTo) of a camera ray (level 0, chunk voxel space): walks the voxels
// of that stretch and tests the blades of the grass voxels the ray dips into. hit: the nearest
// blade, when there is one.
bool VoxelGrassSegment(VoxelChunk c, float3 bmin, float3 bmax, float3 o, float3 d, float tFrom, float tTo, inout VoxelHit hit)
{
    d = VoxelSafeDir(d);
    float3 invD = 1.0 / d;
    int3 stepDir = int3(d.x > 0 ? 1 : -1, d.y > 0 ? 1 : -1, d.z > 0 ? 1 : -1);
    float tips = VoxelGrassTips();
    float t = tFrom;
    int3 p = (int3)floor(o + d * (tFrom + 1e-4));

    [loop]
    for (int i = 0; i < VOXEL_GRASS_SEGMENT_VOXELS && t < tTo; i++)
    {
        if (VoxelOutside(p, bmin, bmax))
            break;

        float3 tExit3 = ((float3)p + (float3)(stepDir > 0) - o) * invD;
        float tExit = min(tExit3.x, min(tExit3.y, tExit3.z));
        bool isUniform;
        if (VoxelFetchMarch(c, 0u, p, isUniform) == VOXEL_ID_GRASS
            && min(o.y + d.y * t, o.y + d.y * tExit) - (float)p.y < tips)
        {
            VoxelGrassBlade blade; float tBlade; int row, along;
            float fade = VoxelGrassFade(t * c.originSize.w);
            if (VoxelGrassTrace(c, p, o, d, invD, t, min(tExit, tTo), fade, 0.0, blade, tBlade, row, along))
            {
                hit = (VoxelHit)0;
                hit.t = tBlade; hit.axis = blade.axis; hit.posVoxel = o + d * tBlade;
                VoxelGrassFillHit(c, p, blade, row, along, VoxelGrassTexels(), hit);
                return true;
            }
        }

        int axis = (tExit3.x <= tExit3.y && tExit3.x <= tExit3.z) ? 0 : (tExit3.y <= tExit3.z ? 1 : 2);
        p += stepDir * VoxelAxisMask(axis);
        t = tExit;
    }
    return false;
}

// First opaque (or alpha-mask passing) surface or grass blade along the ray inside [bmin, bmax).
// See VoxelMarchVoxels for the parameters.
bool VoxelMarchOpaque(VoxelChunk c, uint level, float3 bmin, float3 bmax, float3 o, float3 d, float tMin, out VoxelHit hit,
                      bool skipStartSolid = false, float stopAtMaskedBefore = 0.0, float stopAtGrassBefore = 0.0)
{
    float2 grassSpan = float2(-1.0, -1.0);
    bool found = VoxelMarchVoxels(c, level, bmin, bmax, o, d, tMin, hit, skipStartSolid, stopAtMaskedBefore, stopAtGrassBefore, grassSpan);
    if (grassSpan.x >= 0.0)
    {
        float tTo = found ? min(grassSpan.y, hit.t) : grassSpan.y;
        if (VoxelGrassSegment(c, bmin, bmax, o, d, grassSpan.x, tTo, hit))
            return true;
    }
    return found;
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

#define VOXEL_SHADOW_BRICKS 3   // VoxelSunShadowRay detail: bricks only

// 1 when nothing blocks the ray from posWS along dirWS (towards the sun), 0 otherwise.
// Walks the chunk grid and marches every loaded chunk it crosses. detail: voxel level to march
// (0 = full resolution, 1 = half, 2 = quarter) or VOXEL_SHADOW_BRICKS (8x8x8 bricks only).
float VoxelSunShadowRay(float3 posWS, float3 dirWS, int detail, float ignoreDistance)
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
            // Occluders right next to the receiver are ignored: on gentle voxel slopes every
            // 1-block step would otherwise shadow the next one and draw stripes.
            float ignore = max(ignoreDistance, 0.0);
            if (detail >= VOXEL_SHADOW_BRICKS)
            {
                // Also skip the receiver's own brick so a surface does not shadow itself.
                float3 o = (posWS - c.originSize.xyz) / c.originSize.w;
                if (VoxelMarchCoarse(c, o, d, max(8.0, ignore / c.originSize.w)))
                    return 0.0;
            }
            else
            {
                // Coarser levels march fewer, bigger voxels. They also skip the first coarse
                // voxel: it can bulge above the full resolution surface it starts from.
                uint level = VoxelClampLevel(c, (uint)detail);
                float scale = VoxelLevelScale(level);
                float vs = c.originSize.w * scale;
                float3 o = (posWS - c.originSize.xyz) / vs;
                float3 bmin, bmax;
                VoxelChunkBounds(c, bmin, bmax);
                bmin = floor(bmin / scale);
                bmax = ceil(bmax / scale);
                VoxelHit hit;
                if (VoxelMarchOpaque(c, level, bmin, bmax, o, d, max(ignore / vs, level > 0u ? 1.0 : 0.0), hit, true)
                    && hit.t * vs < _VoxelSunShadowDistance)
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

// Shadow ray detail for a receiver at a camera distance: full resolution near the camera,
// _VoxelFarShadowDetail further out.
int VoxelShadowDetail(float dist)
{
    return dist > _VoxelShadowDetailDistance ? (int)_VoxelFarShadowDetail : 0;
}

// Sun shadow with distance based detail (VoxelShadowDetail), none beyond _VoxelShadowMaxDistance.
float VoxelSunShadow(float3 posWS, float3 normalWS, float3 sunDirWS)
{
    if (dot(normalWS, sunDirWS) <= 0.0)
        return 1.0;

    float dist = distance(posWS, GetCameraPositionWS());
    if (dist > _VoxelShadowMaxDistance)
        return 1.0;

    return VoxelSunShadowRay(posWS + normalWS * 0.01, sunDirWS, VoxelShadowDetail(dist), _VoxelShadowIgnoreDistance);
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

    return VoxelSunShadowRay(posWS + normalWS * 0.02, sunDirWS, VoxelShadowDetail(dist), 0.0);
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
        return VoxelMinLevel(c);

    float3 bmin, bmax;
    VoxelUnpackBounds(packedBounds, bmin, bmax);
    float3 centerWS = c.originSize.xyz + (bmin + bmax) * 0.5 * c.originSize.w;
    float dist = distance(centerWS, GetCameraPositionWS());

    uint level = dist > _VoxelLodDistances.y ? 2u : (dist > _VoxelLodDistances.x ? 1u : 0u);
    if (_VoxelLodDistances.z > 0.0 && dist > _VoxelLodDistances.z)
        level = 3u;
    return VoxelClampLevel(c, level);
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

// Ray differentials of the camera ray through a pixel: how its origin and direction change one
// pixel to the right (x) and up (y). Hardware derivatives of the hit position are useless here
// (they jump at every voxel edge), but the camera ray itself is smooth across the screen.
struct VoxelRayDiff
{
    float3 ro, rd;
    float3 dOx, dOy;
    float3 dDx, dDy;
};

// Call at the top of the fragment shader, before any discard or divergent loop.
VoxelRayDiff VoxelCameraRayDiff(float3 positionWS)
{
    VoxelRayDiff r;
    float tMin;
    VoxelCameraRay(positionWS, r.ro, r.rd, tMin);
    r.dOx = ddx(r.ro); r.dOy = ddy(r.ro);
    r.dDx = ddx(r.rd); r.dDy = ddy(r.rd);
    return r;
}

// Pixel footprint on a face of the given axis at posWS, in face coordinates (blocks):
// the ray differentials transferred onto the face plane.
void VoxelFaceFootprint(VoxelRayDiff r, int axis, float3 posWS, out float2 dx, out float2 dy)
{
    float t = dot(posWS - r.ro, r.rd);
    float dn = r.rd[axis];
    dn = abs(dn) < 1e-4 ? (dn < 0.0 ? -1e-4 : 1e-4) : dn;

    float3 px = r.dOx + t * r.dDx;
    float3 py = r.dOy + t * r.dDy;
    px -= r.rd * (px[axis] / dn);
    py -= r.rd * (py[axis] / dn);
    dx = VoxelWorldFaceCoords(axis, px);
    dy = VoxelWorldFaceCoords(axis, py);
}

// Pixel art filtering: texels stay flat inside and blend over one screen pixel at their
// edges (no jagged texel borders up close), and fall back to plain mip mapped filtering once a
// texel is smaller than a pixel.
float2 VoxelSharpTexelUV(float2 uv, float2 dx, float2 dy, float size)
{
    float2 px = uv * size;
    float2 fw = clamp(max(abs(dx), abs(dy)) * size, 1e-5, 1.0);   // texels per pixel
    float2 seam = floor(px + 0.5);
    px = seam + clamp((px - seam) / fw, -0.5, 0.5);
    return px / size;
}

// Palette surface (BlockSurface.cs): the block's slice of _Textures holds shade (r) and palette
// position (g) as plain values, and its colours come from a lookup baked from
// BlockSurface.Colour by (position, shade), linear colour.
bool VoxelHasSurface(uint id) { return (_VoxelBlockInfo[id] & (1u << 24)) != 0u; }

float3 VoxelSurfaceColour(uint id, float position, float shade)
{
    float n = _VoxelSurfaceLutSize;
    float2 uv = (saturate(float2(position, shade)) * (n - 1.0) + 0.5) / n;
    return SAMPLE_TEXTURE2D_ARRAY_LOD(_VoxelSurfaceLut, sampler_VoxelSurfaceLut, uv, id, 0).rgb;
}

// Small and large scale colours of a palette surface from its two grey texture samples.
void VoxelSurfaceColours(uint id, inout float4 small, inout float4 large)
{
    float4 pp = _VoxelSurfaceParams[id];
    float patch = saturate(large.g * pp.x + pp.y);
    small.rgb = VoxelSurfaceColour(id, lerp(patch, small.g, pp.z), small.r);
    large.rgb = VoxelSurfaceColour(id, patch, large.r);
}

// Average linear colour of a block texture (its smallest mip), palette applied.
float3 VoxelBlockTextureMean(uint id)
{
    float4 m = SAMPLE_TEXTURE2D_ARRAY_LOD(_Textures, sampler_Textures, float2(0.5, 0.5), id, 16);
    if (!VoxelHasSurface(id))
        return m.rgb;
    float4 large = m;
    VoxelSurfaceColours(id, m, large);
    return m.rgb;
}

float4 VoxelSampleTiled(uint id, float2 p, float scale, float2 dx, float2 dy, float size)
{
    float2 uv = frac(p / scale);
    dx /= scale; dy /= scale;
    return SAMPLE_TEXTURE2D_ARRAY_GRAD(_Textures, sampler_Textures, VoxelSharpTexelUV(uv, dx, dy, size), id, dx, dy);
}

// Block texture with large scale variation: the same texture sampled at a small and a large
// tiling and blended, which breaks up repetition across big areas. dx / dy: pixel footprint in
// face coordinates (VoxelFaceFootprint), used to pick and filter the mip levels.
float4 VoxelSampleBlockTexture(uint id, int axis, float3 posWS, float2 dx, float2 dy)
{
    float2 p = VoxelWorldFaceCoords(axis, posWS);
    float smallScale = max(_VoxelTexVariation.x, 0.01);
    float largeScale = max(_VoxelTexVariation.y, 0.01);
    float blend = saturate(_VoxelTexVariation.z);

    float w = 0.0, h = 0.0, slices = 0.0;
    _Textures.GetDimensions(w, h, slices);

    float4 small = VoxelSampleTiled(id, p, smallScale, dx, dy, w);
    bool palette = VoxelHasSurface(id);
    if (blend <= 0.0 && !palette)
        return small;
    float4 large = VoxelSampleTiled(id, p, largeScale, dx, dy, w);

    // Palette surface: the large scale grey picks the patch colour, the small scale grey shades
    // the texel.
    if (palette)
    {
        VoxelSurfaceColours(id, small, large);
        if (blend <= 0.0)
            return small;
    }

    if (_VoxelTexVariation.w > 0.5)
        return float4(small.rgb * lerp(1.0, large.rgb * 2.0, blend), small.a);   // multiply (overlay-like)
    return lerp(small, large, blend);
}

float4 VoxelSampleBlockTexture(uint id, int axis, float3 posWS, VoxelRayDiff r)
{
    float2 dx, dy;
    VoxelFaceFootprint(r, axis, posWS, dx, dy);
    return VoxelSampleBlockTexture(id, axis, posWS, dx, dy);
}

#include "VoxelRelief.hlsl"

float4 VoxelSampleAlbedo(VoxelHit hit, float3 hitWS, VoxelRayDiff r)
{
    // Relief columns take the colour of their texel on the face they were carved from, walls
    // included. The footprint is taken at the actual hit so the mip matches the visible size
    // (on the card itself for grass blades, which are coloured from the top face below them).
    if (hit.refined != 0u)
    {
        float2 dx, dy;
        VoxelFaceFootprint(r, hit.grass != 0u ? hit.axis : hit.texAxis, hitWS, dx, dy);
        return VoxelSampleBlockTexture(hit.id, hit.texAxis, hit.texPosWS, dx, dy);
    }
    return VoxelSampleBlockTexture(hit.id, hit.axis, hitWS, r);
}

// --------------------------------------------------------------------------------------
// Corner ambient occlusion
// --------------------------------------------------------------------------------------

float _VoxelAmbientOcclusion;   // strength of the per-corner voxel occlusion (0 = off)

// Any voxel near the chunk, looked up through the chunk grid when it lies outside (also
// diagonally, unlike VoxelFetchNeighbor). Air when that chunk is not loaded.
uint VoxelFetchGrid(VoxelChunk c, uint level, int3 p)
{
    if (VoxelInChunk(c, level, p))
        return VoxelFetch(c, level, p);

    float3 world = c.originSize.xyz + ((float3)p + 0.5) * (c.originSize.w * VoxelLevelScale(level));
    int slot = VoxelLookupChunk((int3)floor(world / _VoxelChunkWorldSize));
    if (slot < 0)
        return 0u;

    VoxelChunk nc = _VoxelChunks[slot];
    uint nLevel = VoxelClampLevel(nc, level);
    int3 q = (int3)floor((world - nc.originSize.xyz) / (nc.originSize.w * VoxelLevelScale(nLevel)));
    return VoxelFetch(nc, nLevel, clamp(q, 0, VoxelLevelRes(nc, nLevel) - 1));
}

float VoxelOccluderAt(VoxelChunk c, uint level, int3 p)
{
    uint vis = VoxelBlockVisibility(VoxelFetchGrid(c, level, p));
    return (vis == VOXEL_VIS_OPAQUE || vis == VOXEL_VIS_STACKED) ? 1.0 : 0.0;
}

// Classic voxel corner occlusion: each corner of the hit face is darkened by the blocks next to
// it in the layer in front of the face, then interpolated across the face. 1 = unoccluded.
float VoxelCornerOcclusion(VoxelChunk c, uint level, VoxelHit hit, float3 rayDir)
{
    if (_VoxelAmbientOcclusion <= 0.0 || VoxelBlockVisibility(hit.id) == VOXEL_VIS_STACKED)
        return 1.0;

    // Relief hits sit inside the block: use the exposed face they were carved from. Grass blades
    // take the occlusion of the top face they grow from.
    int a = hit.refined != 0u ? hit.texAxis : hit.axis;
    bool facePositive = rayDir[a] < 0.0;
    float3 p = hit.posVoxel;
    if (hit.grass != 0u)
    {
        a = 1;
        facePositive = true;
        p.y = floor((hit.texPosWS.y - c.originSize.y) / (c.originSize.w * VoxelLevelScale(level)));
    }

    int3 m = VoxelAxisMask(a);
    int frontA = facePositive ? (int)ceil(p[a] - 1e-3) : (int)floor(p[a] + 1e-3) - 1;
    int3 front = (int3)floor(p) * (1 - m) + frontA * m;

    int ua = a == 0 ? 1 : 0;
    int wa = a == 2 ? 1 : 2;
    int3 eu = VoxelAxisMask(ua);
    int3 ew = VoxelAxisMask(wa);

    float su0 = VoxelOccluderAt(c, level, front - eu);
    float su1 = VoxelOccluderAt(c, level, front + eu);
    float sw0 = VoxelOccluderAt(c, level, front - ew);
    float sw1 = VoxelOccluderAt(c, level, front + ew);
    float c00 = VoxelOccluderAt(c, level, front - eu - ew);
    float c10 = VoxelOccluderAt(c, level, front + eu - ew);
    float c01 = VoxelOccluderAt(c, level, front - eu + ew);
    float c11 = VoxelOccluderAt(c, level, front + eu + ew);

    // Two blocked sides fully close the corner, whatever the diagonal is.
    float4 sides1 = float4(su0, su1, su0, su1);
    float4 sides2 = float4(sw0, sw0, sw1, sw1);
    float4 corner = float4(c00, c10, c01, c11);
    float4 ao = (3.0 - min(sides1 + sides2 + corner, 3.0) - sides1 * sides2 * (1.0 - corner)) / 3.0;

    float fu = smoothstep(0.0, 1.0, saturate(p[ua] - front[ua]));
    float fw = smoothstep(0.0, 1.0, saturate(p[wa] - front[wa]));
    float v = lerp(lerp(ao.x, ao.y, fu), lerp(ao.z, ao.w, fu), fw);
    return lerp(1.0, v, saturate(_VoxelAmbientOcclusion));
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
    // Near the camera the march stops at leaf voxels so the relief can give them thickness, and
    // above grass so it can grow the blades.
    float stopMasked = input.level == 0u ? VoxelReliefMaskedStop(vs) : 0.0;
    float stopGrass = input.level == 0u ? VoxelGrassStop(vs) : 0.0;
    bool found = VoxelMarchOpaque(c, input.level, bmin, bmax, o, rayDirWS, tMinWS / vs, hit, false, stopMasked, stopGrass);

    hitWS = ro + rayDirWS * (hit.t * vs);
    normalWS = VoxelFaceNormal(hit.axis, rayDirWS);

    // Texel relief near the camera. Every pass (depth, normals, forward) refines the same way,
    // so their depths agree.
    if (found && input.level == 0u)
        found = VoxelReliefRefine(c, bmin, bmax, ro, o, rayDirWS, vs, hit, hitWS, normalWS);
    return found;
}

#endif

// VoxelGrass.hlsl
// Grass blades on blocks whose surface decoration is Grass. Included by VoxelRaymarchCore.hlsl.
//
// The air voxel above an exposed grass top (the pack job's grass marker, VOXEL_ID_GRASS) holds a
// few thin blades. A blade is a flat, zero thickness card on an X or Z plane of the block
// texture's texel grid, drawn as pixel art on that plane: a strip of texels, one or more wide at
// the root, narrowing and leaning towards the tip. Blade layouts are baked on the CPU
// (VoxelGrassBlades.cs) into sets; a block picks a set and its blade count by hash of its position,
// so blades are stable and follow edits. A low frequency noise over blocks leaves some grass
// blocks bare (coverage). A blade takes the colour of the top face texel it stands on. Past the
// blades, grass tops are shaded by their patch instead (VoxelGrassFarShade).
//
// Cards are axis aligned, so their hits rebuild from the depth / normals prepass like any voxel
// face, and they stay inside the grass marker voxel, which the grown draw cell boxes contain.
// All positions here are in chunk voxel space (level 0), as in VoxelMarchOpaque.

#ifndef PROGENWORLD_VOXEL_GRASS_INCLUDED
#define PROGENWORLD_VOXEL_GRASS_INCLUDED

float4 _VoxelGrass;       // x: tallest blade (blocks, 0 = off), y: blades per block, z: camera distance, w: fade band
float4 _VoxelGrassShape;  // x: coverage (share of grass blocks with blades), y: widest root (texels), z: most lean (texels), w: root shade
float  _VoxelGrassShadows; // 1 = blades cast sun shadows
float  _VoxelGrassPatchHeight; // blades rise towards the densest centre of a coverage patch (1 = edges half as tall)
float  _VoxelGrassFar;     // darkening of grass tops inside a patch past the blades (far stand-in)
float  _VoxelGrassTexels;  // blade grid (texels per block) the blade table was baked for, 0 = no table
StructuredBuffer<uint4> _VoxelGrassBlades;  // VOXEL_GRASS_SETS sets of 4 uint4 = 16 packed blades (VoxelGrassBlades.cs)

#define VOXEL_GRASS_MAX_BLADES 16   // VoxelGrassBlades.MaxBlades
#define VOXEL_GRASS_SETS       256  // VoxelGrassBlades.Sets
#define VOXEL_GRASS_SHADOW_VOXELS 3

struct VoxelGrassBlade
{
    int axis;    // 0: card in an X plane, 2: in a Z plane
    int plane;   // texel line of the card inside the voxel (1 .. texels - 1)
    int along;   // first texel of the root along the card
    int width;   // root width in texels
    int height;  // in texels
    int lean;    // texels the tip is shifted along the card (signed)
    float bend;  // texels the wind adds to the lean at the tip
};

bool VoxelGrassEnabled()
{
    return _VoxelGrass.x > 0.0 && _VoxelGrass.y > 0.0 && _VoxelGrassShape.x > 0.0 && _VoxelGrassTexels >= 2.0;
}

// Camera distance (voxel units) before which the coarse march looks for blades. 0 = never.
float VoxelGrassStop(float vs)
{
    return VoxelGrassEnabled() ? _VoxelGrass.z / vs : 0.0;
}

// Blades shrink into the ground over the fade band.
float VoxelGrassFade(float distWS)
{
    return saturate((_VoxelGrass.z - distWS) / max(_VoxelGrass.w, 1e-3));
}

// Tallest possible blade tip above the grass top (blocks). Blades never leave their voxel.
float VoxelGrassTips()
{
    return min(_VoxelGrass.x, 1.0);
}

// Block texture texels per voxel edge: the blade grid.
// (A voxel is one block, VoxelWorldRenderer: chunkSize / resolution.)
int VoxelGrassTexels()
{
    return (int)_VoxelGrassTexels;
}

uint VoxelGrassHash(int2 p)
{
    uint h = ((uint)p.x * 0x8DA6B343u) ^ ((uint)p.y * 0xD8163841u);
    h ^= h >> 16; h *= 0x7FEB352Du;
    h ^= h >> 15; h *= 0x846CA68Bu;
    h ^= h >> 16;
    return h;
}

// v[axis] for axis 0 or 2. (Dynamic vector indexing would force fxc to unroll the march loops.)
float VoxelGrassPick(float3 v, int axis)
{
    return axis == 0 ? v.x : v.z;
}

float VoxelGrassUnit(uint h)
{
    return (float)(h & 0xFFFFu) / 65535.0;
}

// World block coordinate of a voxel of chunk c, for hashing (same in every chunk that sees it).
int3 VoxelGrassBlock(VoxelChunk c, int3 p)
{
    return (int3)floor(c.originSize.xyz / c.originSize.w + 0.5) + p;
}

// Coverage patches over ~6 blocks. weight: 0 bare .. 1 inside a patch (soft edge). core: 0 at
// the patch edge .. 1 a little way in, from the smooth noise only so it changes gradually across
// blocks. h: a random per block.
void VoxelGrassPatch(int2 xz, out float weight, out float core, out uint h)
{
    float2 q = (float2)xz / 6.0;
    int2 i = (int2)floor(q);
    float2 f = smoothstep(0.0, 1.0, q - (float2)i);
    float n = lerp(lerp(VoxelGrassUnit(VoxelGrassHash(i)), VoxelGrassUnit(VoxelGrassHash(i + int2(1, 0))), f.x),
                   lerp(VoxelGrassUnit(VoxelGrassHash(i + int2(0, 1))), VoxelGrassUnit(VoxelGrassHash(i + int2(1, 1))), f.x), f.y);
    float coverage = _VoxelGrassShape.x;
    core = saturate((coverage - n) / 0.15);

    h = VoxelGrassHash(xz + int2(0x3779, 0x1F2B));
    n = lerp(n, VoxelGrassUnit(h), 0.25);
    const float edge = 0.12;
    weight = saturate((coverage * (1.0 + edge) - n) / edge);
}

// Blades on a grass block: the patch weight times blades per block, dithered by the block random
// so the average matches the setting. set: the block's layout in the blade table.
// heightScale: blades reach full height at the densest centre of a patch, shorter towards its edges.
int VoxelGrassBladeCount(int3 block, out float heightScale, out uint set)
{
    float weight, core; uint h;
    VoxelGrassPatch(block.xz, weight, core, h);
    heightScale = lerp(1.0 / (1.0 + max(_VoxelGrassPatchHeight, 0.0)), 1.0, core);
    set = VoxelGrassHash(int2((int)h, block.y)) & (VOXEL_GRASS_SETS - 1);
    float count = _VoxelGrass.y * weight + VoxelGrassUnit(h >> 16);
    return min((int)count, VOXEL_GRASS_MAX_BLADES);
}

// Packed blade fields (VoxelGrassBlades.cs).
bool VoxelGrassOnX(uint data)  { return (data & 1u) != 0u; }
int VoxelGrassPlane(uint data) { return (int)((data >> 1) & 63u); }
int VoxelGrassRank(uint data)  { return (int)(data >> 28); }

uint VoxelGrassPickBlade(uint4 v, uint i)
{
    return i == 0u ? v.x : (i == 1u ? v.y : (i == 2u ? v.z : v.w));
}

// Blade i of a set (a separate small load; the sets are read whole in VoxelGrassTrace).
uint VoxelGrassLoadBlade(uint set, uint i)
{
    return VoxelGrassPickBlade(_VoxelGrassBlades[set * 4u + (i >> 2)], i & 3u);
}

// bend: the wind bend of the block (VoxelWindGrassBend); taller blades bend further.
VoxelGrassBlade VoxelGrassBladeShape(uint data, int axis, int plane, int texels, float fade, float heightScale, float2 bend)
{
    VoxelGrassBlade b;
    b.axis = axis;
    b.plane = plane;
    b.along = (int)((data >> 7) & 63u);
    b.width = (int)((data >> 13) & 7u);
    b.lean = (int)((data >> 16) & 15u) - 8;
    float heightRandom = (float)((data >> 20) & 255u) / 255.0;
    b.height = min((int)floor(_VoxelGrass.x * heightScale * texels * fade * lerp(0.45, 1.0, heightRandom) + 0.5), texels);
    b.bend = (axis == 0 ? bend.x : bend.y) * lerp(0.6, 1.0, heightRandom);
    return b;
}

// Whether the point q (voxel space, on the card's plane) of voxel p is on the blade. row / along:
// the texel there.
bool VoxelGrassCovers(VoxelGrassBlade b, int3 p, float3 q, int texels, out int row, out int along)
{
    row = (int)floor((q.y - (float)p.y) * texels);
    along = (int)floor((VoxelGrassPick(q, 2 - b.axis) - VoxelGrassPick((float3)p, 2 - b.axis)) * texels);
    if (row < 0 || row >= b.height)
        return false;

    // Narrows from the root width to one texel; leans (and bends in the wind) along a quadratic
    // curve, by whole texels. Texels bent out of the block are cut off.
    float f = ((float)row + 0.5) / (float)b.height;
    int shift = (int)round(((float)b.lean + b.bend) * f * f);
    int w = max(1, b.width - (int)floor((float)b.width * row / (float)b.height));
    int start = b.along + shift;
    return along >= start && along < start + w;
}

// Fills a camera / prepass hit on a blade. The colour comes from the top face texel the blade
// stands on (on the card's -axis side), the occlusion darkens towards the root.
void VoxelGrassFillHit(VoxelChunk c, int3 p, VoxelGrassBlade b, int row, int along, int texels, inout VoxelHit hit)
{
    float across = ((float)b.plane - 0.5) / texels;
    float alongPos = ((float)along + 0.5) / texels;
    float3 tex = (float3)p + (b.axis == 0 ? float3(across, 0, alongPos) : float3(alongPos, 0, across));
    tex.y += 0.5 / texels;

    hit.id = VoxelFetchAny(c, 0u, p - int3(0, 1, 0));
    hit.refined = 1u;
    hit.grass = 1u;
    hit.texAxis = 1;
    hit.texPosWS = c.originSize.xyz + tex * c.originSize.w;
    hit.occlusion = 1.0 - _VoxelGrassShape.w * (1.0 - ((float)row + 0.5) / (float)b.height);
}

// Nearest blade of grass voxel p crossed by the ray o + d t for t in [tIn, tOut). skipNear: cards
// closer than this to o along their axis are ignored (the card a shadow ray starts on).
bool VoxelGrassTrace(VoxelChunk c, int3 p, float3 o, float3 d, float3 invD, float tIn, float tOut, float fade,
                     float skipNear, out VoxelGrassBlade blade, out float tHit, out int row, out int along)
{
    blade = (VoxelGrassBlade)0;
    tHit = tOut;
    row = 0; along = 0;
    if (fade <= 0.0)
        return false;

    int texels = VoxelGrassTexels();
    int3 block = VoxelGrassBlock(c, p);
    float heightScale; uint set;
    int count = VoxelGrassBladeCount(block, heightScale, set);
    float2 bend = VoxelWindGrassBend((float2)block.xz);
    if (count <= 0)
        return false;

    // Only the part of the ray below this block's tallest blade can hit one.
    float tallest = min(floor(_VoxelGrass.x * heightScale * texels * fade + 0.5), (float)texels);
    float tTop = ((float)p.y + tallest / texels - o.y) * invD.y;
    if (d.y < 0.0)
        tIn = max(tIn, tTop);
    else
        tOut = min(tOut, tTop);
    if (tIn >= tOut)
        return false;
    tHit = tOut;

    // Plane culling: the texel planes that part of the ray crosses, per card axis.
    float3 a = o + d * tIn - (float3)p;
    float3 b = o + d * tOut - (float3)p;
    int kx0 = max(1, (int)ceil(min(a.x, b.x) * texels - 1e-3));
    int kx1 = min(texels - 1, (int)floor(max(a.x, b.x) * texels + 1e-3));
    int kz0 = max(1, (int)ceil(min(a.z, b.z) * texels - 1e-3));
    int kz1 = min(texels - 1, (int)floor(max(a.z, b.z) * texels + 1e-3));

    // Read the whole set (4 loads) and cull its blades in registers: shown (rank), on a crossed
    // plane and crossed within [tIn, tOut). Loading blade by blade in a loop costs a dependent
    // read per blade, which was most of the grass cost.
    uint4 q[4];
    [unroll]
    for (uint k = 0u; k < 4u; k++)
        q[k] = _VoxelGrassBlades[set * 4u + k];

    uint candidates = 0u;
    [unroll]
    for (uint i = 0u; i < VOXEL_GRASS_MAX_BLADES; i++)
    {
        uint data = q[i >> 2][i & 3u];
        bool onX = VoxelGrassOnX(data);
        int plane = VoxelGrassPlane(data);
        bool crossed = onX ? (plane >= kx0 && plane <= kx1) : (plane >= kz0 && plane <= kz1);
        if (crossed && VoxelGrassRank(data) < count)
            candidates |= 1u << i;
    }

    // Full test of the few candidates.
    bool found = false;
    [loop]
    while (candidates != 0u)
    {
        uint i = firstbitlow(candidates);
        candidates &= candidates - 1u;
        uint data = VoxelGrassLoadBlade(set, i);
        int axis = VoxelGrassOnX(data) ? 0 : 2;
        int plane = VoxelGrassPlane(data);

        float x = VoxelGrassPick((float3)p, axis) + (float)plane / texels;
        float oa = VoxelGrassPick(o, axis);
        if (abs(x - oa) < skipNear)
            continue;
        float t = (x - oa) * VoxelGrassPick(invD, axis);
        if (t < tIn || t >= tHit)
            continue;

        VoxelGrassBlade bl = VoxelGrassBladeShape(data, axis, plane, texels, fade, heightScale, bend);
        int r, al;
        if (VoxelGrassCovers(bl, p, o + d * t, texels, r, al))
        {
            found = true;
            blade = bl; tHit = t; row = r; along = al;
        }
    }
    return found;
}

// Rebuilds a blade hit at the prepass surface pv (voxel space) of grass voxel p, on a card of the
// given axis. False when no blade is there (the march then decides).
bool VoxelGrassResolve(VoxelChunk c, int3 p, float3 pv, int axis, float distWS, inout VoxelHit hit)
{
    float fade = VoxelGrassFade(distWS);
    if (!VoxelGrassEnabled() || fade <= 0.0 || axis == 1)
        return false;

    int texels = VoxelGrassTexels();
    float across = (VoxelGrassPick(pv, axis) - VoxelGrassPick((float3)p, axis)) * texels;
    int k = (int)round(across);
    if (k < 1 || k > texels - 1 || abs(across - (float)k) >= 0.25)
        return false;

    int3 block = VoxelGrassBlock(c, p);
    float heightScale; uint set;
    int count = VoxelGrassBladeCount(block, heightScale, set);
    float2 bend = VoxelWindGrassBend((float2)block.xz);
    bool onX = axis == 0;

    // The shown cards on plane k only.
    [loop]
    for (uint i = 0u; i < VOXEL_GRASS_MAX_BLADES; i++)
    {
        uint data = VoxelGrassLoadBlade(set, i);
        if (VoxelGrassOnX(data) != onX || VoxelGrassPlane(data) != k || VoxelGrassRank(data) >= count)
            continue;

        VoxelGrassBlade b = VoxelGrassBladeShape(data, axis, k, texels, fade, heightScale, bend);
        int r, a;
        if (VoxelGrassCovers(b, p, pv, texels, r, a))
        {
            VoxelGrassFillHit(c, p, b, r, a, texels, hit);
            return true;
        }
    }
    return false;
}

// Sun shadow the blades cast on a surface near grass (1 = lit). Walks the few voxels the sun ray
// crosses next to the surface; the regular voxel shadow rays ignore the blades.
float VoxelGrassSunShadow(VoxelChunk c, float3 posWS, float3 sunDirWS)
{
    // Grass is read from full resolution data, which distant chunks don't upload.
    if (!VoxelGrassEnabled() || _VoxelGrassShadows < 0.5 || sunDirWS.y <= 0.0 || VoxelMinLevel(c) > 0u)
        return 1.0;
    float fade = VoxelGrassFade(distance(posWS, GetCameraPositionWS()));
    if (fade <= 0.0)
        return 1.0;

    float vs = c.originSize.w;
    float3 o = (posWS - c.originSize.xyz) / vs;
    float3 d = VoxelSafeDir(sunDirWS);
    float3 invD = 1.0 / d;
    int3 stepDir = int3(d.x > 0 ? 1 : -1, d.y > 0 ? 1 : -1, d.z > 0 ? 1 : -1);
    int3 p = (int3)floor(o + d * 1e-4);
    float tIn = 1e-4;
    float skipNear = 0.1 / VoxelGrassTexels();   // the card the surface itself lies on

    [loop]
    for (int i = 0; i < VOXEL_GRASS_SHADOW_VOXELS; i++)
    {
        // Risen above the tips of this layer's blades: nothing left to hit.
        if (o.y + d.y * tIn - (float)p.y >= VoxelGrassTips())
            return 1.0;

        float3 tExit3 = ((float3)p + (float3)(stepDir > 0) - o) * invD;
        float tOut = min(tExit3.x, min(tExit3.y, tExit3.z));

        uint id = VoxelFetchAny(c, 0u, p);
        uint vis = VoxelBlockVisibility(id);
        if (vis == VOXEL_VIS_OPAQUE)
            return 1.0;   // the regular shadow rays take over

        VoxelGrassBlade b; float t; int r, a;
        if (VoxelGrassAir(c, p, id)
            && VoxelGrassTrace(c, p, o, d, invD, tIn, tOut, fade, skipNear, b, t, r, a))
            return 0.0;

        int axis = (tExit3.x <= tExit3.y && tExit3.x <= tExit3.z) ? 0 : (tExit3.y <= tExit3.z ? 1 : 2);
        p += stepDir * VoxelAxisMask(axis);
        tIn = tOut;
    }
    return 1.0;
}

// Far stand-in: past the blades a grass top is darkened where its patch would grow them, fading in
// as the blades shrink away, so patches still read far away and the cutoff blends in. Per block,
// not per texel, so it does not shimmer. Multiplies the albedo.
half VoxelGrassFarShade(uint id, float3 hitWS, float3 normalWS)
{
    if (_VoxelGrassFar <= 0.0 || !VoxelGrassEnabled() || normalWS.y < 0.5 || VoxelBlockDecoration(id) != VOXEL_DECOR_GRASS)
        return 1.0;
    float far = 1.0 - VoxelGrassFade(distance(hitWS, GetCameraPositionWS()));
    if (far <= 0.0)
        return 1.0;

    float weight, core; uint h;
    VoxelGrassPatch((int2)floor(hitWS.xz), weight, core, h);
    return 1.0 - _VoxelGrassFar * far * weight * lerp(0.8, 1.0, VoxelGrassUnit(h >> 16));
}

#endif

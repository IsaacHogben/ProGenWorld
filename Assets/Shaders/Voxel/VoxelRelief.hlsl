// VoxelRelief.hlsl
// Texel relief for nearby voxel surfaces. Included by VoxelRaymarchCore.hlsl.
//
// Every exposed face of an opaque block is carved inwards on the block texture's texel grid:
// each texel is a small column whose depth comes from the texture's brightness (baked into
// _VoxelHeights by BlockTextureArrayBuilder, 1 = surface, 0 = deepest). A sub-point of a solid
// voxel is empty when it is closer to one of the voxel's exposed faces than that face's carve
// depth at the point. Hidden faces are never carved, so flat ground stays seamless across
// blocks and only real edges and corners get notched.
//
// Alpha masked blocks (leaves) get thickness instead: a sub-point of a masked voxel is solid
// when it lies within the leaf thickness of one of the voxel's exposed faces and that face's
// alpha mask passes at the point. Each face card becomes a slab of small cubes, so the holes
// show the sides of the opaque texels.
//
// Carving only removes material and leaf slabs sit behind their face, so a refined hit is never
// in front of the box face it was found through: culling and conservative depth stay valid.

#ifndef PROGENWORLD_VOXEL_RELIEF_INCLUDED
#define PROGENWORLD_VOXEL_RELIEF_INCLUDED

TEXTURE2D_ARRAY(_VoxelHeights);

float4 _VoxelRelief;         // x: max carve depth (blocks), y: camera distance, z: fade band, w: occlusion strength
float  _VoxelReliefLeaf;     // leaf slab thickness (blocks)
float  _VoxelReliefShadows;  // 1 = trace sun shadows inside the relief
float  _VoxelTextureSize;    // texels per block texture edge (0 = no height data, relief off)

#define VOXEL_RELIEF_STEPS        64
#define VOXEL_RELIEF_SHADOW_STEPS 24
#define VOXEL_RELIEF_ATTEMPTS     4

#define VOXEL_RELIEF_HIT       0
#define VOXEL_RELIEF_OPEN      1   // reached an empty voxel: the ray left the relief shell
#define VOXEL_RELIEF_STEPS_OUT 2

struct VoxelReliefParams
{
    float ts;           // texel size in world units (one relief column)
    float depthTexels;  // max carve depth in texels, faded by camera distance
    float leafTexels;   // leaf slab thickness in texels, faded by camera distance
};

// Cached data of the voxel the relief march is in.
struct VoxelReliefCell
{
    int3 v;
    uint id;
    bool solid;    // opaque block
    bool masked;   // alpha masked block (leaves)
    uint exposed;  // bit per face: 0 -X, 1 +X, 2 -Y, 3 +Y, 4 -Z, 5 +Z
};

struct VoxelReliefResult
{
    int    status;
    float  t;        // world distance from the march origin to the hit face
    int    axis;     // axis of the face that was hit
    int    texAxis;  // exposed face whose texture colours the hit column (-1 = none)
    float  depth;    // hit column depth below that face, in texels
    uint   id;
    float3 center;   // centre of the hit column cell (exact texel centre for texturing)
};

bool VoxelReliefLeavesEnabled()
{
    return _VoxelTextureSize > 0.0 && _VoxelReliefLeaf > 0.0;
}

bool VoxelReliefSetup(float3 posWS, out VoxelReliefParams rp)
{
    rp.ts = 0.0;
    rp.depthTexels = 0.0;
    rp.leafTexels = 0.0;
    if (_VoxelTextureSize <= 0.0)
        return false;

    float dist = distance(posWS, GetCameraPositionWS());
    float fade = saturate((_VoxelRelief.y - dist) / max(_VoxelRelief.z, 1e-3));
    rp.ts = max(_VoxelTexVariation.x, 0.01) / _VoxelTextureSize;
    rp.depthTexels = max(_VoxelRelief.x, 0.0) * fade / rp.ts;
    rp.leafTexels = max(_VoxelReliefLeaf, 0.0) * fade / rp.ts;
    return rp.depthTexels >= 0.5 || rp.leafTexels >= 0.5;
}

VoxelReliefCell VoxelReliefNewCell()
{
    VoxelReliefCell cell;
    cell.v = int3(0x7FFFFFFF, 0x7FFFFFFF, 0x7FFFFFFF);
    cell.id = 0u;
    cell.solid = false;
    cell.masked = false;
    cell.exposed = 0u;
    return cell;
}

void VoxelReliefLoadCell(VoxelChunk c, int3 v, inout VoxelReliefCell cell)
{
    cell.v = v;
    cell.id = VoxelFetchAny(c, 0u, v);
    uint vis = VoxelBlockVisibility(cell.id);
    cell.solid = vis == VOXEL_VIS_OPAQUE;
    cell.masked = vis == VOXEL_VIS_STACKED;
    cell.exposed = 0u;
    if (!cell.solid && !cell.masked)
        return;

    [unroll]
    for (int f = 0; f < 6; f++)
    {
        int3 n = v + VoxelAxisMask(f >> 1) * ((f & 1) ? 1 : -1);
        if (VoxelBlockVisibility(VoxelFetchAny(c, 0u, n)) != VOXEL_VIS_OPAQUE)
            cell.exposed |= 1u << f;
    }
}

// Height (0..1) of the texel under posWS on a face of the given axis. Same texel grid as
// VoxelSampleBlockTexture's small scale sample.
float VoxelReliefHeight(uint id, int axis, float3 posWS)
{
    float2 uv = frac(VoxelWorldFaceCoords(axis, posWS) / max(_VoxelTexVariation.x, 0.01));
    int size = (int)_VoxelTextureSize;
    int2 texel = min((int2)(uv * size), size - 1);
    return LOAD_TEXTURE2D_ARRAY(_VoxelHeights, texel, id).r;
}

// Whether the relief cell centred at centerWS is solid. Also returns the exposed face that
// colours it (texAxis, -1 when the voxel has none) and the cell's depth below that face in texels.
bool VoxelReliefSolid(VoxelChunk c, inout VoxelReliefCell cell, float3 centerWS, VoxelReliefParams rp,
                      out int texAxis, out float depth)
{
    texAxis = -1;
    depth = 0.0;

    float vs = c.originSize.w;
    float3 pv = (centerWS - c.originSize.xyz) / vs;
    int3 v = (int3)floor(pv);
    if (any(v != cell.v))
        VoxelReliefLoadCell(c, v, cell);
    if (!cell.solid && !cell.masked)
        return false;

    float3 local = (pv - (float3)v) * vs;   // world units from the voxel's min corner
    float nearest = 1e9;

    if (cell.masked)
    {
        // Leaf slabs: solid within the thickness of an exposed face where its mask passes.
        [unroll]
        for (int f = 0; f < 6; f++)
        {
            if ((cell.exposed & (1u << f)) == 0u)
                continue;

            int a = f >> 1;
            float d = (f & 1) ? (vs - local[a]) : local[a];
            float k = floor(d / rp.ts);
            if (k < rp.leafTexels && k < nearest && VoxelMaskPass(c, 0u, cell.id, a, pv))
            {
                nearest = k;
                texAxis = a;
            }
        }
        return texAxis >= 0;
    }

    [unroll]
    for (int f = 0; f < 6; f++)
    {
        if ((cell.exposed & (1u << f)) == 0u)
            continue;

        int a = f >> 1;
        float d = (f & 1) ? (vs - local[a]) : local[a];
        float k = floor(d / rp.ts);
        if (k < rp.depthTexels)
        {
            float h = VoxelReliefHeight(cell.id, a, centerWS);
            float carve = floor((1.0 - h) * rp.depthTexels + 0.5);
            if (k < carve)
                return false;
        }
        if (k < nearest)
        {
            nearest = k;
            texAxis = a;
        }
    }

    depth = texAxis >= 0 ? nearest : 0.0;
    return true;
}

// DDA over the relief grid (cells of rp.ts) from o along d. startAxis is the face the ray
// entered the first cell through.
VoxelReliefResult VoxelReliefMarch(VoxelChunk c, float3 o, float3 d, int startAxis, VoxelReliefParams rp, int maxSteps)
{
    VoxelReliefResult r = (VoxelReliefResult)0;
    r.status = VOXEL_RELIEF_STEPS_OUT;
    r.texAxis = -1;

    d = VoxelSafeDir(d);
    float3 invD = 1.0 / d;
    int3 stepDir = int3(d.x > 0 ? 1 : -1, d.y > 0 ? 1 : -1, d.z > 0 ? 1 : -1);

    // Nudge into the first cell so a hit that lies exactly on a face starts on the far side of it.
    int3 sc = (int3)floor((o + d * (rp.ts * 0.02)) / rp.ts);
    float3 tNext = (((float3)sc + (float3)(stepDir > 0)) * rp.ts - o) * invD;
    float3 tDelta = abs(invD) * rp.ts;

    VoxelReliefCell cell = VoxelReliefNewCell();
    float t = 0.0;
    int axis = startAxis;

    [loop]
    for (int i = 0; i < maxSteps; i++)
    {
        float3 center = ((float3)sc + 0.5) * rp.ts;
        int texAxis; float depth;
        if (VoxelReliefSolid(c, cell, center, rp, texAxis, depth))
        {
            r.status = VOXEL_RELIEF_HIT;
            r.t = t; r.axis = axis; r.texAxis = texAxis; r.depth = depth;
            r.id = cell.id; r.center = center;
            return r;
        }
        if (!cell.solid && !cell.masked)
        {
            r.status = VOXEL_RELIEF_OPEN;
            r.t = t; r.axis = axis;
            return r;
        }

        axis = (tNext.x <= tNext.y && tNext.x <= tNext.z) ? 0 : (tNext.y <= tNext.z ? 1 : 2);
        int3 m = VoxelAxisMask(axis);
        t = dot(tNext, (float3)m);
        sc += stepDir * m;
        tNext += tDelta * (float3)m;
    }

    r.t = t; r.axis = axis;
    return r;
}

// Camera distance (voxel units) before which the coarse march stops at masked voxels so the
// relief can give them thickness. 0 = never stop.
float VoxelReliefMaskedStop(float vs)
{
    return VoxelReliefLeavesEnabled() ? _VoxelRelief.y / vs : 0.0;
}

// Plain coarse march (thin leaf cards) from the entry of a masked voxel the relief gave up on.
// Restarts just before the entry so the mask is tested on the face the ray came in through.
bool VoxelReliefFallback(VoxelChunk c, float3 bmin, float3 bmax, float3 ro, float3 o, float3 rd, float vs, float tEntry,
                         inout VoxelHit hit, inout float3 hitWS, inout float3 normalWS)
{
    VoxelHit h;
    if (!VoxelMarchOpaque(c, 0u, bmin, bmax, o, rd, tEntry - 1e-3, h))
        return false;
    hit = h;
    hitWS = ro + rd * (hit.t * vs);
    normalWS = VoxelFaceNormal(hit.axis, rd);
    return true;
}

// Refines a coarse level 0 camera hit with the texel relief. o / tMin parameters match the
// coarse march (chunk voxel space). The coarse hit is either an opaque face or the entry of a
// masked voxel (the coarse march stops there when leaf thickness is on). Grass blade hits are
// final. Returns false when the ray slips past everything in this draw cell (another cell's box
// then draws the pixel).
bool VoxelReliefRefine(VoxelChunk c, float3 bmin, float3 bmax, float3 ro, float3 o, float3 rd, float vs,
                       inout VoxelHit hit, inout float3 hitWS, inout float3 normalWS)
{
    float stopMasked = VoxelReliefMaskedStop(vs);
    float stopGrass = VoxelGrassStop(vs);

    [loop]
    for (int attempt = 0; attempt < VOXEL_RELIEF_ATTEMPTS; attempt++)
    {
        if (hit.grass != 0u)
            return true;
        bool masked = VoxelBlockVisibility(hit.id) == VOXEL_VIS_STACKED;
        VoxelReliefParams rp;
        if (!VoxelReliefSetup(hitWS, rp))
        {
            // Out of relief range: masked entries still need their thin card surface.
            return masked ? VoxelReliefFallback(c, bmin, bmax, ro, o, rd, vs, hit.t, hit, hitWS, normalWS) : true;
        }

        VoxelReliefResult r = VoxelReliefMarch(c, hitWS, rd, hit.axis, rp, VOXEL_RELIEF_STEPS);

        if (r.status == VOXEL_RELIEF_HIT)
        {
            hitWS += rd * r.t;
            hit.t += r.t / vs;
            hit.id = r.id;
            hit.axis = r.axis;
            hit.posVoxel = (hitWS - c.originSize.xyz) / vs;
            hit.refined = 1u;
            hit.texAxis = r.texAxis >= 0 ? r.texAxis : r.axis;
            hit.texPosWS = r.center;
            hit.occlusion = 1.0 - _VoxelRelief.w * saturate(r.depth / max(rp.depthTexels, 1.0));
            normalWS = VoxelFaceNormal(r.axis, rd);
            return true;
        }

        if (r.status == VOXEL_RELIEF_STEPS_OUT)
        {
            // Grazing ray or a long way through foliage: keep the coarse surface.
            return masked ? VoxelReliefFallback(c, bmin, bmax, ro, o, rd, vs, hit.t, hit, hitWS, normalWS) : true;
        }

        // Slipped through a carved edge or a leaf hole into open space: find the next surface
        // behind it. (Starts a hair past the boundary so rounding cannot put it back in the voxel.)
        VoxelHit next;
        if (!VoxelMarchOpaque(c, 0u, bmin, bmax, o, rd, hit.t + r.t / vs + 1e-3, next, false, stopMasked, stopGrass))
            return false;
        hit = next;
        hitWS = ro + rd * (hit.t * vs);
        normalWS = VoxelFaceNormal(hit.axis, rd);
    }

    return VoxelBlockVisibility(hit.id) == VOXEL_VIS_STACKED
        ? VoxelReliefFallback(c, bmin, bmax, ro, o, rd, vs, hit.t, hit, hitWS, normalWS)
        : true;
}

// Sun shadow cast by the relief columns and leaf slabs onto a refined hit (1 = lit).
float VoxelReliefSunShadow(VoxelChunk c, VoxelHit hit, float3 hitWS, float3 normalWS, float3 sunDirWS)
{
    if (hit.refined == 0u || hit.grass != 0u || _VoxelReliefShadows < 0.5 || dot(normalWS, sunDirWS) <= 0.0)
        return 1.0;

    VoxelReliefParams rp;
    if (!VoxelReliefSetup(hitWS, rp))
        return 1.0;

    float3 o = hitWS + normalWS * (rp.ts * 0.05);
    VoxelReliefResult r = VoxelReliefMarch(c, o, sunDirWS, hit.axis, rp, VOXEL_RELIEF_SHADOW_STEPS);
    return r.status == VOXEL_RELIEF_HIT ? 0.0 : 1.0;
}

#endif

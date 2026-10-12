// VoxelPrepass.hlsl
// Reuse of URP's depth + normals prepass, and the screen space voxel sun shadows.
//
// The prepass (DepthNormals) already marched every voxel pixel. The forward pass rebuilds the
// hit from the stored depth and face normal instead of marching the ray a second time: the
// voxel just behind the surface gives the block, the relief cell around it gives the texel.
// Pixels it cannot rebuild (leaf back faces, non-voxel surfaces in front) fall back to the march.
//
// Sun shadows are traced once per pixel of a (possibly reduced resolution) screen texture by
// VoxelScreenShadows.shader between the prepass and the opaques, and upsampled into URP's
// _ScreenSpaceShadowmapTexture together with the object shadows.
//
// Include after VoxelRaymarchCore.hlsl and URP's DeclareDepthTexture / DeclareNormalsTexture.
// Normals are read without octahedral decoding (forward renderers); anything that is not an
// axis aligned voxel normal falls back to the march.

#ifndef PROGENWORLD_VOXEL_PREPASS_INCLUDED
#define PROGENWORLD_VOXEL_PREPASS_INCLUDED

float _VoxelPrepassReuse;    // 1 while this camera's prepass depth / normals can be reused
float _VoxelScreenShadows;   // 1 while _ScreenSpaceShadowmapTexture holds this camera's resolved shadows
float _VoxelShadowPixelated; // 1 = sun shadows snapped to the block texture's texels
TEXTURE2D(_VoxelScreenShadowTexture);   // r: sun visibility, g: texel key, b: linear eye depth

#define VOXEL_RESOLVE_HIT   0   // hit rebuilt, and it lies in this draw cell
#define VOXEL_RESOLVE_OTHER 1   // another draw cell owns the hit (or nothing is there): discard
#define VOXEL_RESOLVE_TRACE 2   // could not rebuild the hit: march the ray

bool VoxelDepthIsFar(float rawDepth)
{
#if UNITY_REVERSED_Z
    return rawDepth <= 0.0;
#else
    return rawDepth >= 1.0;
#endif
}

// Surface position for a prepass depth. Perspective: along the pixel's ray at the linear eye
// depth, which keeps full precision far from the world origin.
float3 VoxelPrepassPosition(float2 screenUV, float rawDepth)
{
    float3 p = ComputeWorldSpacePosition(screenUV, rawDepth, UNITY_MATRIX_I_VP);
    if (unity_OrthoParams.w > 0.5)
        return p;

    float3 cam = GetCameraPositionWS();
    float3 rd = normalize(p - cam);
    float eye = LinearEyeDepth(rawDepth, _ZBufferParams);
    return cam + rd * (eye / dot(rd, -UNITY_MATRIX_V[2].xyz));
}

// Axis aligned face normal from the prepass normals texture. False for other surfaces.
bool VoxelPrepassNormal(float2 screenUV, out float3 normalWS, out int axis)
{
    float3 n = SampleSceneNormals(screenUV);
    axis = VoxelMajorAxis(n);
    normalWS = (float3)VoxelAxisMask(axis) * (n[axis] > 0.0 ? 1.0 : -1.0);
    return abs(n[axis]) > 0.9;
}

// World size of one block texture texel (one relief column).
float VoxelTexelWorldSize()
{
    return max(_VoxelTexVariation.x, 0.01) / max(_VoxelTexelCount, 1.0);
}

// Fills the relief fields of a level 0 hit the way VoxelReliefRefine would have. The relief
// cell is the texel cube just behind the surface.
void VoxelResolveRelief(VoxelChunk c, float3 hitWS, float3 normalWS, inout VoxelHit hit)
{
    VoxelReliefParams rp;
    if (!VoxelReliefSetup(hitWS, rp))
        return;

    float3 inside = hitWS - normalWS * (rp.ts * 0.5);
    float3 center = (floor(inside / rp.ts) + 0.5) * rp.ts;
    VoxelReliefCell cell = VoxelReliefNewCell();
    int texAxis; float depth;
    if (!VoxelReliefSolid(c, cell, center, rp, texAxis, depth))
        return;   // the prepass kept the coarse surface (grazing ray): shade it unrefined

    hit.refined = 1u;
    hit.texAxis = texAxis >= 0 ? texAxis : hit.axis;
    hit.texPosWS = center;
    hit.occlusion = 1.0 - _VoxelRelief.w * saturate(depth / max(rp.depthTexels, 1.0));
}

// Rebuilds this pixel's hit from the prepass for the draw cell [bmin, bmax) of chunk c.
int VoxelResolvePrepass(VoxelChunk c, uint level, float3 bmin, float3 bmax, float2 screenUV, float rawDepth,
                        out VoxelHit hit, out float3 hitWS, out float3 normalWS)
{
    hit = (VoxelHit)0;
    hitWS = 0;
    normalWS = 0;
    if (_VoxelPrepassReuse < 0.5)
        return VOXEL_RESOLVE_TRACE;

    // The prepass drew the same boxes: no depth means this box's ray misses as well.
    if (VoxelDepthIsFar(rawDepth))
        return VOXEL_RESOLVE_OTHER;

    int axis;
    if (!VoxelPrepassNormal(screenUV, normalWS, axis))
        return VOXEL_RESOLVE_TRACE;

    hitWS = VoxelPrepassPosition(screenUV, rawDepth);
    float vs = c.originSize.w * VoxelLevelScale(level);
    float3 pv = (hitWS - c.originSize.xyz) / vs;
    int3 q = (int3)floor(pv - normalWS * 0.01);

    uint id = VoxelFetchGrid(c, level, q);
    uint vis = VoxelBlockVisibility(id);
    if (level == 0u && VoxelGrassAir(c, q, id))
    {
        // A grass blade: find the card under the surface.
        if (VoxelOutside(q, bmin, bmax))
            return VOXEL_RESOLVE_OTHER;
        hit.axis = axis;
        hit.posVoxel = pv;
        return VoxelGrassResolve(c, q, pv, axis, distance(hitWS, GetCameraPositionWS()), hit)
            ? VOXEL_RESOLVE_HIT : VOXEL_RESOLVE_TRACE;
    }
    if (vis != VOXEL_VIS_OPAQUE && vis != VOXEL_VIS_STACKED)
        return VOXEL_RESOLVE_TRACE;   // e.g. the far face of a leaf block: the surface is in front
    if (VoxelOutside(q, bmin, bmax))
        return VOXEL_RESOLVE_OTHER;

    hit.id = id;
    hit.axis = axis;
    hit.posVoxel = pv;
    hit.occlusion = 1.0;
    if (level == 0u)
        VoxelResolveRelief(c, hitWS, normalWS, hit);
    return VOXEL_RESOLVE_HIT;
}

// --------------------------------------------------------------------------------------
// Sun shadows
// --------------------------------------------------------------------------------------

// Shadow ray origin: the hit, or with pixelated shadows the centre of its texel on the face,
// so every texel is either lit or shadowed as a whole, like a texture pixel.
float3 VoxelShadowOrigin(float3 hitWS, float3 normalWS)
{
    if (_VoxelShadowPixelated < 0.5)
        return hitWS;
    float ts = VoxelTexelWorldSize();
    float3 snapped = (floor(hitWS / ts) + 0.5) * ts;
    return lerp(snapped, hitWS, abs(normalWS));   // keep the coordinate across the face
}

// Identifies the texel (and face direction) a surface point belongs to. Used to upsample
// pixelated shadows without blurring across texel edges.
float VoxelShadowKey(float3 hitWS, float3 normalWS)
{
    float ts = VoxelTexelWorldSize();
    int3 t = (int3)floor((hitWS - normalWS * (ts * 0.5)) / ts);
    uint face = (uint)(dot(normalWS, float3(1, 2, 3)) + 3.5);
    uint h = ((uint)t.x * 73856093u) ^ ((uint)t.y * 19349663u) ^ ((uint)t.z * 83492791u) ^ (face * 2654435761u);
    return (float)(h & 0xFFFFFFu);
}

// Voxel sun shadow at a surface, traced in place (no screen shadow texture available). Grass
// blades are lit as if facing up (VoxelGrassLightNormal), so their shadow uses that normal too.
half VoxelTraceSurfaceShadow(VoxelChunk c, VoxelHit hit, float3 hitWS, float3 normalWS, float3 sunDir)
{
    float3 n = hit.grass != 0u ? float3(0, 1, 0) : normalWS;
    half s = VoxelSunShadow(VoxelShadowOrigin(hitWS, n), n, sunDir);
    if (s > 0.0)
        s *= VoxelReliefSunShadow(c, hit, hitWS, normalWS, sunDir);
    if (s > 0.0)
        s *= VoxelGrassSunShadow(c, hitWS, sunDir);
    return s;
}

// 0..5 for the six axis aligned face directions of a voxel normal.
float VoxelFaceId(float3 normalWS)
{
    int axis = VoxelMajorAxis(normalWS);
    return (float)(axis * 2 + (normalWS[axis] > 0.0 ? 1 : 0));
}

// Upsampled voxel shadow at a full resolution pixel, from the 4x4 nearest shadow texels:
//  - only texels that traced the same kind of surface count (the same voxel face direction, or
//    an object). Without this, ground right at the foot of a wall blends in the wall's samples,
//    which face away from the sun and are lit, and draws a lit gap between wall and shadow;
//  - of those, texels that traced the same block texel win (crisp pixelated shadow edges);
//  - the rest are weighted by distance and depth similarity.
half VoxelSampleScreenShadow(float2 screenUV, float3 hitWS, float3 normalWS, bool voxelFace)
{
    uint w, h;
    _VoxelScreenShadowTexture.GetDimensions(w, h);
    int2 size = int2(w, h);

    float2 p = screenUV * (float2)size - 0.5;
    int2 base = (int2)floor(p);
    float2 f = p - (float2)base;

    float eye = LinearEyeDepth(hitWS, UNITY_MATRIX_V);
    bool pixelated = _VoxelShadowPixelated > 0.5 && voxelFace;
    float key = pixelated ? VoxelShadowKey(hitWS, normalWS) : -2.0;   // -2 never matches
    float face = voxelFace ? VoxelFaceId(normalWS) : -1.0;

    float sum = 0.0, sumW = 0.0, match = 0.0, matchN = 0.0, other = 0.0, otherW = 0.0;
    [unroll]
    for (int i = 0; i < 16; i++)
    {
        int2 o = int2(i & 3, i >> 2) - 1;
        float4 s = LOAD_TEXTURE2D(_VoxelScreenShadowTexture, clamp(base + o, 0, size - 1));
        float2 d = (float2)o - f;
        float near = max(0.0, 1.0 - 0.25 * dot(d, d));   // falls off over ~2 texels
        float similar = 1.0 / (1e-3 + 50.0 * abs(s.b - eye) / max(eye, 1e-3));
        float wgt = near * similar + 1e-6;
        other += s.r * wgt;
        otherW += wgt;
        if (s.a != face)
            continue;
        if (s.g == key)
        {
            match += s.r * near;
            matchN += near;
        }
        sum += s.r * wgt;
        sumW += wgt;
    }
    if (matchN > 0.0)
        return match / matchN;
    return sumW > 0.0 ? sum / sumW : other / otherW;   // no texel of this surface nearby: best guess
}

#endif

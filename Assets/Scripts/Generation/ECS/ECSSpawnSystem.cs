using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

public partial class ECSSpawnSystem : SystemBase
{
    // Populated from a bootstrap MonoBehaviour
    // Key: category << 8 | decorationType
    public static Dictionary<int, Mesh> Meshes = new();
    public static Dictionary<int, Material> Materials = new();

    // Takes a read-only view — copies into blob, does not take ownership
    public static void RequestSpawn(int3 chunkCoord, float3 worldOffset,
                                    NativeArray<ECSSpawnPoint> spawns)
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null) return;

        var builder = new BlobBuilder(Allocator.Temp);
        ref var root = ref builder.ConstructRoot<ECSSpawnBlob>();
        var arr = builder.Allocate(ref root.Points, spawns.Length);
        for (int i = 0; i < spawns.Length; i++)
            arr[i] = spawns[i]; // copy

        var blobRef = builder.CreateBlobAssetReference<ECSSpawnBlob>(Allocator.Persistent);
        builder.Dispose();
        // spawns NOT disposed here — caller (MeshData pool) owns it

        var em = world.EntityManager;
        var requestEntity = em.CreateEntity();
        em.AddComponentData(requestEntity, new ECSSpawnRequest
        {
            ChunkCoord = chunkCoord,
            WorldOffset = worldOffset,
            Points = blobRef
        });
    }

    // Called from HandleECSSpawns before RequestSpawn on remesh
    public static void DestroyChunk(int3 coord)
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null) return;

        var em = world.EntityManager;
        using var query = em.CreateEntityQuery(
            ComponentType.ReadOnly<ECSDecoration>(),
            ComponentType.ReadOnly<ECSChunkOwner>()
        );
        using var entities = query.ToEntityArray(Allocator.Temp);
        using var owners = query.ToComponentDataArray<ECSChunkOwner>(Allocator.Temp);

        for (int i = 0; i < owners.Length; i++)
            if (owners[i].ChunkCoord.Equals(coord))
                em.DestroyEntity(entities[i]);
    }

    EntityArchetype _spawnArchetype;

    protected override void OnCreate()
    {
        _spawnArchetype = EntityManager.CreateArchetype(
            typeof(ECSDecoration),
            typeof(ECSDecorationData),
            typeof(ECSChunkOwner),
            typeof(LocalTransform),
            typeof(LocalToWorld)
        // No PhysicsCollider
        );
        RequireForUpdate<ECSSpawnRequest>();
    }

    protected override void OnUpdate()
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        // --- Pass 1: collect requests, queue destroys, consume blob ---
        // We must NOT call EntityManager structural changes inside this loop
        var requestsToProcess = new NativeList<(int3 coord, float3 offset,
                                BlobAssetReference<ECSSpawnBlob> blob, Entity requestEntity)>
                                (Allocator.Temp);

        foreach (var (request, requestEntity) in
            SystemAPI.Query<RefRO<ECSSpawnRequest>>().WithEntityAccess())
        {
            var req = request.ValueRO;

            // Queue destroys via ECB — safe inside iteration
            DestroyChunkDecorations(req.ChunkCoord, ecb);

            // Collect for processing after iteration ends
            requestsToProcess.Add((req.ChunkCoord, req.WorldOffset, req.Points, requestEntity));
        }

        // --- Iteration is done — structural changes are now safe ---

        for (int r = 0; r < requestsToProcess.Length; r++)
        {
            var (chunkCoord, worldOffset, blobRef, requestEntity) = requestsToProcess[r];
            ref var blob = ref blobRef.Value;

            var rng = new Unity.Mathematics.Random(
                (uint)(chunkCoord.x * 73856093 ^ chunkCoord.y * 19349663 ^ chunkCoord.z * 83492791));

            using var entities = EntityManager.CreateEntity(
                _spawnArchetype, blob.Points.Length, Allocator.Temp);

            for (int i = 0; i < blob.Points.Length; i++)
            {
                ref var pt = ref blob.Points[i];

                float3 worldPos = worldOffset + pt.position;
                worldPos.x += rng.NextFloat(-0.3f, 0.3f);
                worldPos.z += rng.NextFloat(-0.3f, 0.3f);

                float scale = rng.NextFloat(0.7f, 1.3f);
                float yaw = rng.NextFloat(0f, math.PI2);

                EntityManager.SetComponentData(entities[i],
                    LocalTransform.FromPositionRotationScale(
                        worldPos,
                        quaternion.RotateY(yaw),
                        scale));

                EntityManager.AddComponentData(entities[i], new RenderBounds
                {
                    Value = new AABB
                    {
                        Center = new float3(0, 0.5f, 0), // centre of blade
                        Extents = new float3(0.5f, 1f, 0.5f)  // half-extents
                    }
                });

                EntityManager.SetComponentData(entities[i], new ECSDecorationData
                {
                    Category = pt.category,
                    DecorationType = pt.decorationType,
                    BiomeIndex = pt.biomeIndex
                });

                EntityManager.SetComponentData(entities[i], new ECSChunkOwner
                {
                    ChunkCoord = chunkCoord
                });

                int key = (pt.category << 8) | pt.decorationType;
                if (Meshes.TryGetValue(key, out var mesh) &&
                    Materials.TryGetValue(key, out var mat))
                {
                    var renderMeshArray = new RenderMeshArray(new[] { mat }, new[] { mesh });
                    var renderMeshDesc = new RenderMeshDescription(
                        shadowCastingMode: UnityEngine.Rendering.ShadowCastingMode.Off,
                        receiveShadows: false
                    );

                    RenderMeshUtility.AddComponents(
                        entities[i],
                        EntityManager,
                        renderMeshDesc,
                        renderMeshArray,
                        MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0)
                    );
                }
            }

            blobRef.Dispose();
            ecb.DestroyEntity(requestEntity);
        }

        requestsToProcess.Dispose();

        ecb.Playback(EntityManager);
        ecb.Dispose();
    }

    void DestroyChunkDecorations(int3 coord, EntityCommandBuffer ecb)
    {
        using var query = EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<ECSDecoration>(),
            ComponentType.ReadOnly<ECSChunkOwner>()
        );
        using var entities = query.ToEntityArray(Allocator.Temp);
        using var owners = query.ToComponentDataArray<ECSChunkOwner>(Allocator.Temp);

        for (int i = 0; i < owners.Length; i++)
            if (owners[i].ChunkCoord.Equals(coord))
                ecb.DestroyEntity(entities[i]); // ECB, not EntityManager directly
    }
}
using System.Collections.Generic;
using UnityEngine;

public class ECSDecorationBootstrap : MonoBehaviour
{
    [System.Serializable]
    public struct DecorationEntry
    {
        public DecorationCategory category;
        public DecorationType.Vegetation vegetationType;
        public Mesh mesh;
        public Material material;
    }

    [SerializeField] DecorationEntry[] entries;

    void Awake()
    {
        foreach (var entry in entries)
        {
            if (entry.mesh == null || entry.material == null)
            {
                Debug.LogWarning($"[ECSDecorationBootstrap] Entry {entry.category}/{entry.vegetationType} is missing mesh or material, skipping.");
                continue;
            }

            int key = ((int)entry.category << 8) | (int)entry.vegetationType;
            ECSSpawnSystem.Meshes[key] = entry.mesh;
            ECSSpawnSystem.Materials[key] = entry.material;
        }

        Debug.Log($"[ECSDecorationBootstrap] Registered {entries.Length} decoration types.");
    }
}
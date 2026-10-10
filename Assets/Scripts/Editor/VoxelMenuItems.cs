using UnityEditor;
using UnityEngine;

public static class VoxelMenuItems
{
    [MenuItem("GameObject/ProGenWorld/Voxel Render Settings", false, 10)]
    static void CreateRenderSettings(MenuCommand command)
    {
        var existing = Object.FindFirstObjectByType<VoxelRenderSettings>();
        if (existing != null)
        {
            Selection.activeGameObject = existing.gameObject;
            EditorGUIUtility.PingObject(existing);
            Debug.Log("[ProGenWorld] The scene already has Voxel Render Settings - selected it.");
            return;
        }

        var go = new GameObject("Voxel Render Settings");
        go.AddComponent<VoxelRenderSettings>();
        GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);
        Undo.RegisterCreatedObjectUndo(go, "Create Voxel Render Settings");
        Selection.activeGameObject = go;
    }
}

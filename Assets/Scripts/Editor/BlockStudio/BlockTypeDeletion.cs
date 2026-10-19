#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Deletes a BlockType. Block ids are the enum's implicit values, so every later block moves
/// down one id: serialized BlockType fields in assets (biomes, L-systems, region settings...),
/// prefabs and open scenes are renumbered to match, and references to the deleted block become
/// Air. Then the BlockDatabase entry and the enum line are removed and scripts recompile.
/// </summary>
public static class BlockTypeDeletion
{
    static readonly string[] Names = Enum.GetNames(typeof(BlockType));

    /// <summary>Scripts that name the block in code ("path:line"); deleting it would break the build.</summary>
    public static List<string> CodeReferences(string name, string enumPath)
    {
        var hits = new List<string>();
        var pattern = new Regex(@"\bBlockType\." + Regex.Escape(name) + @"\b");
        foreach (var file in Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories))
        {
            string path = file.Replace('\\', '/');
            if (path == enumPath)
                continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (pattern.IsMatch(lines[i]))
                    hits.Add($"{path}:{i + 1}");
        }
        return hits;
    }

    /// <summary>Why the enum cannot be edited automatically (null when it can).</summary>
    public static string EnumProblem(string enumPath)
    {
        if (enumPath == null)
            return "Could not find the BlockType enum (BlockTypes.cs).";
        string body = EnumBody(File.ReadAllText(enumPath), out _, out _);
        return body.Contains("=") ? "The BlockType enum has explicit values; delete the block by hand." : null;
    }

    static string EnumBody(string text, out int open, out int close)
    {
        int start = text.IndexOf("enum BlockType", StringComparison.Ordinal);
        open = text.IndexOf('{', start);
        close = text.IndexOf('}', open);
        return text.Substring(open + 1, close - open - 1);
    }

    /// <summary>
    /// Deletes block id from the enum, the database and every serialized reference. Returns a
    /// summary for the log. Scripts recompile afterwards.
    /// </summary>
    public static string Delete(BlockDatabase db, int id, bool deleteSurface, string enumPath)
    {
        string name = Names[id];
        var report = new List<string> { $"Deleted block type {name} (id {id}); ids above it moved down one." };

        // Surface (only when no other block uses it and it lives in the Block Studio folders).
        var surface = db.blocks[id].surface;
        bool shared = surface != null && db.blocks.Where((b, i) => i != id && b.surface == surface).Any();

        // 1. Renumber serialized BlockType values everywhere but the database.
        int changed = 0;
        var touched = new List<string>();
        foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset == null || asset == db || asset is GameObject || asset is Component)
                    continue;
                int n = Remap(asset, id);
                if (n > 0) { changed += n; touched.Add(path); }
            }
        }
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null)
                continue;
            int n = 0;
            foreach (var c in root.GetComponentsInChildren<Component>(true))
                if (c != null)
                    n += Remap(c, id);
            if (n > 0) { changed += n; touched.Add(path); }
        }
        for (int s = 0; s < EditorSceneManager.sceneCount; s++)
        {
            var scene = EditorSceneManager.GetSceneAt(s);
            if (!scene.isLoaded)
                continue;
            int n = 0;
            foreach (var go in scene.GetRootGameObjects())
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                    if (c != null)
                        n += Remap(c, id);
            if (n > 0)
            {
                changed += n;
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                touched.Add(scene.path + " (saved)");
            }
        }
        report.Add(changed > 0
            ? $"Renumbered {changed} block reference(s) in: {string.Join(", ", touched.Distinct())}"
            : "No assets referenced blocks above it.");

        // 2. Database: drop the entry, later entries keep their block (one id lower).
        Undo.RecordObject(db, "Delete Block Type");
        var list = db.blocks.ToList();
        list.RemoveAt(id);
        for (int i = id; i < list.Count; i++)
        {
            var e = list[i];
            if ((int)e.type > id)
                e.type = (BlockType)((int)e.type - 1);
            list[i] = e;
        }
        db.blocks = list.ToArray();
        EditorUtility.SetDirty(db);

        if (deleteSurface && surface != null && !shared)
        {
            string folder = BlockSurfaceAssets.FolderOf(surface);
            if (folder.StartsWith(BlockSurfaceAssets.Root + "/"))
            {
                AssetDatabase.DeleteAsset(folder);
                report.Add($"Deleted surface folder {folder}.");
            }
            else
                report.Add($"Kept surface {AssetDatabase.GetAssetPath(surface)} (outside {BlockSurfaceAssets.Root}).");
        }
        else if (surface != null)
            report.Add(shared ? $"Kept surface {surface.name} (used by other blocks)." : $"Kept surface {surface.name}.");
        AssetDatabase.SaveAssets();

        // 3. Enum line, then recompile.
        File.WriteAllText(enumPath, RemoveFromEnum(File.ReadAllText(enumPath), name));
        AssetDatabase.ImportAsset(enumPath);
        AssetDatabase.Refresh();

        report.Add("Saved worlds or edit data that store block ids are not renumbered.");
        return string.Join("\n", report);
    }

    /// <summary>The source with the value's line removed from the BlockType enum.</summary>
    public static string RemoveFromEnum(string text, string name)
    {
        string body = EnumBody(text, out int open, out int close);
        string newBody = Regex.Replace(body, @"^[ \t]*" + Regex.Escape(name) + @"[ \t]*,?[ \t]*(//[^\r\n]*)?\r?(\n|$)", "", RegexOptions.Multiline);
        newBody = Regex.Replace(newBody, @",(\s*)$", "$1");   // no trailing comma after the last value
        return text.Substring(0, open + 1) + newBody + text.Substring(close);
    }

    static bool IsBlockType(SerializedProperty p)
        => p.propertyType == SerializedPropertyType.Enum && p.enumNames.Length == Names.Length && p.enumNames.SequenceEqual(Names);

    /// <summary>Renumbers BlockType fields of one object. Returns the number changed.</summary>
    internal static int Remap(Object target, int removed)
    {
        var so = new SerializedObject(target);
        var it = so.GetIterator();
        int changed = 0;
        bool enter = true;
        while (it.Next(enter))
        {
            enter = it.propertyType != SerializedPropertyType.String;
            if (!IsBlockType(it))
                continue;
            int v = it.intValue;
            if (v == removed) { it.intValue = 0; changed++; }
            else if (v > removed) { it.intValue = v - 1; changed++; }
        }
        if (changed > 0)
        {
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
        }
        return changed;
    }
}
#endif

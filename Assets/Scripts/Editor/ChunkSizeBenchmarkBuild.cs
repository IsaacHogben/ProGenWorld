// ChunkSizeBenchmarkBuild.cs
// Builds a Windows player for the chunk size and render benchmarks from a copy of the main scene
// with ChunkSizeBenchmark and RenderBenchmark objects added (each idle unless its flag is passed).
// The main scene and project settings are left unchanged.
//
// Unity.exe -batchmode -quit -projectPath <proj> -executeMethod ChunkSizeBenchmarkBuild.Build -benchBuildPath <dir>/ProGenWorld.exe

using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ChunkSizeBenchmarkBuild
{
    const string SourceScene = "Assets/Scenes/Scene.unity";
    const string BenchScene = "Assets/Scenes/ChunkSizeBenchmark_Temp.unity";

    public static void Build()
    {
        string outPath = null;
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-benchBuildPath") outPath = args[i + 1];
        if (string.IsNullOrEmpty(outPath))
            throw new ArgumentException("-benchBuildPath is required");

        bool frameTiming = PlayerSettings.enableFrameTimingStats;
        try
        {
            AssetDatabase.CopyAsset(SourceScene, BenchScene);
            var scene = EditorSceneManager.OpenScene(BenchScene, OpenSceneMode.Single);
            new GameObject("Chunk Size Benchmark").AddComponent<ChunkSizeBenchmark>();
            new GameObject("Render Benchmark").AddComponent<RenderBenchmark>();
            EditorSceneManager.SaveScene(scene);

            PlayerSettings.enableFrameTimingStats = true; // GPU frame times in the player

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { BenchScene },
                locationPathName = outPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            });

            Debug.Log($"[ChunkBench] Build {report.summary.result}: {report.summary.totalErrors} errors, {report.summary.totalTime}");
            if (report.summary.result != BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }
        finally
        {
            PlayerSettings.enableFrameTimingStats = frameTiming;
            AssetDatabase.SaveAssets();
            AssetDatabase.DeleteAsset(BenchScene);
        }
    }
}

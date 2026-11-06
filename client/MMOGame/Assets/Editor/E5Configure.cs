using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class E5Configure
{
    public static void Apply()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Settings"))
        {
            AssetDatabase.CreateFolder("Assets", "Settings");
        }

        if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
        {
            AssetDatabase.CreateFolder("Assets", "Scenes");
        }

        var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
        AssetDatabase.CreateAsset(renderer, "Assets/Settings/UniversalRenderer.asset");
        var pipeline = UniversalRenderPipelineAsset.Create(renderer);
        AssetDatabase.CreateAsset(pipeline, "Assets/Settings/UniversalRenderPipeline.asset");
        GraphicsSettings.renderPipelineAsset = pipeline;
        QualitySettings.renderPipeline = pipeline;

        PlayerSettings.companyName = "YunjinFanxing";
        PlayerSettings.productName = "MMOGame";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.yunjinfanxing.mmogame");
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

        var playerSettings = Resources.FindObjectsOfTypeAll<PlayerSettings>().FirstOrDefault();
        if (playerSettings != null)
        {
            var serialized = new SerializedObject(playerSettings);
            var input = serialized.FindProperty("activeInputHandler");
            if (input != null)
            {
                input.intValue = 1;
                serialized.ApplyModifiedProperties();
            }
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, "Assets/Scenes/Empty.unity");
        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene("Assets/Scenes/Empty.unity", true)
        };

        AssetDatabase.SaveAssets();
        EditorApplication.Exit(0);
    }

    public static void BuildAndroid()
    {
        Directory.CreateDirectory("Builds");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/Empty.unity" },
            locationPathName = "Builds/MMOGame.apk",
            target = BuildTarget.Android,
            options = BuildOptions.None
        });
        var ok = report.summary.result == BuildResult.Succeeded;
        Debug.Log(ok
            ? "apk=" + report.summary.outputPath
            : "apk_failed=" + report.summary.result);
        EditorApplication.Exit(ok ? 0 : 1);
    }
}

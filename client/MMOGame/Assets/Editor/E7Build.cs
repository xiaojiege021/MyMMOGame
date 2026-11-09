using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using HybridCLR.Commands;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// E7 阶段：用装好补丁的 il2cpp 打一个 Android IL2CPP 包，确认补丁真的参与编译。
public static class E7Build
{
    const string ApkDir = "/tmp/mmo-e7-apk";

    public static void Run()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        }

        // 生成 hybridclr 需要的桥接代码、link.xml 和 AOT 泛型引用
        PrebuildCommand.GenerateAll();
        var marker = HybridCLR.SettingsUtil.GeneratedCppDir + "/MethodBridge.inc";
        Debug.Log("build_methodbridge=" + (File.Exists(marker) ? "present" : "missing") + " file=" + marker);

        // 编 Android 的热更 dll，并复制成 Launch 包的原生资源
        var hotDll = E6Build.PrepareHotDll(BuildTarget.Android);
        var hotSize = new FileInfo(hotDll).Length;
        Debug.Log("build_hotdll=" + (hotSize > 0 ? "ok" : "missing") + " size=" + hotSize);

        // Launch AB 重打（含 Boss 场景与热更 dll），StreamingAssets 跟着更新
        var ab = E6Build.BuildAndroidAb();
        if (!ab.Success)
        {
            Debug.Log("build_result=AbFailed error=" + ab.ErrorInfo);
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log("build_ab=ok " + ab.OutputPackageDirectory);

        // 上一次可能留了同名目录或 .aab，先清掉；另外明确要 apk 而不是 Gradle 工程
        if (Directory.Exists(ApkDir))
        {
            Directory.Delete(ApkDir, true);
        }

        Directory.CreateDirectory(ApkDir);
        EditorUserBuildSettings.buildAppBundle = false;
        EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
        var apk = Path.Combine(ApkDir, "MMOGame.apk");
        var options = new BuildPlayerOptions
        {
            scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
            locationPathName = apk,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;
        Debug.Log("build_result=" + summary.result + " errors=" + summary.totalErrors
            + " size_mb=" + (summary.totalSize / (1024 * 1024)) + " sec=" + (long)summary.totalTime.TotalSeconds);

        if (summary.result != BuildResult.Succeeded)
        {
            EditorApplication.Exit(1);
            return;
        }

        if (!File.Exists(apk))
        {
            Debug.Log("build_apk=fail reason=输出不是 apk 文件: " + apk);
            EditorApplication.Exit(1);
            return;
        }

        using (var zip = ZipFile.OpenRead(apk))
        {
            var so = zip.Entries.FirstOrDefault(e => e.FullName == "lib/arm64-v8a/libil2cpp.so");
            var meta = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("global-metadata.dat"));
            Debug.Log("build_apk=ok path=" + apk
                + " so=" + (so != null ? so.Length.ToString() : "missing")
                + " metadata=" + (meta != null ? meta.Length.ToString() : "missing")
                + " entries=" + zip.Entries.Count);
        }

        EditorApplication.Exit(0);
    }
}

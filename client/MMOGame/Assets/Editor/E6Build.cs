using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using YooAsset;
using YooAsset.Editor;

public static class E6Build
{
    const string PackageName = "Launch";
    const string PackageVersion = "1";
    public const string HotDllDir = "Assets/HotUpdateDlls";

    public static void SelfCheck()
    {
        PrepareHotDll(BuildTarget.Android);
        EnsureCollector();
        var hostRoot = Path.Combine(Path.GetTempPath(), "mmo-e6-host");
        if (Directory.Exists(hostRoot))
        {
            Directory.Delete(hostRoot, true);
        }

        var host = Build(BuildTarget.StandaloneOSX, hostRoot);
        if (!host.Success)
        {
            Debug.Log("launch_check=fail build_host=" + host.ErrorInfo);
            EditorApplication.Exit(1);
            return;
        }

        var android = BuildAndroidAb();
        if (!android.Success)
        {
            Debug.Log("launch_check=fail build_android=" + android.ErrorInfo);
            EditorApplication.Exit(1);
            return;
        }

        var manifestPath = Path.Combine(android.OutputPackageDirectory, PackageName + "_" + PackageVersion + ".json");
        var manifest = File.ReadAllText(manifestPath);
        // P0-2a 起 Launch 里除了 marker 还有 Boss 场景和热更 dll，一样都不能少。
        if (!manifest.Contains(AOT.LaunchSession.MarkerPath)
            || !manifest.Contains("BossBattle")
            || !manifest.Contains("HotUpdate")
            || !AllRegionsIn(manifest)
            || HasLaterRealm(manifest))
        {
            Debug.Log("launch_check=fail android_manifest=" + manifestPath);
            EditorApplication.Exit(1);
            return;
        }

        Directory.CreateDirectory("Temp");
        File.WriteAllText(AOT.LaunchLoader.HostRootFile, Path.Combine(hostRoot, PackageName));
        Debug.Log("launch_android=ok " + android.OutputPackageDirectory);
        EditorApplication.EnterPlaymode();
    }

    /// <summary>编译热更 dll 并复制成原生资源，进 Launch 包。E6/E7 与核对脚本共用。</summary>
    public static string PrepareHotDll(BuildTarget target)
    {
        HybridCLR.Commands.CompileDllCommand.CompileDll(target);
        var output = HybridCLR.SettingsUtil.GetHotUpdateDllsOutputDirByTarget(target);
        var source = Path.Combine(output, "HotUpdate.dll");
        if (!File.Exists(source))
        {
            Debug.Log("hot_dll=missing " + source);
            EditorApplication.Exit(1);
            return source;
        }

        Directory.CreateDirectory(HotDllDir);
        File.Copy(source, Path.Combine(HotDllDir, "HotUpdate.bytes"), true);
        Debug.Log("hot_dll=ok " + source);
        return source;
    }

    /// <summary>把 Launch 包打进 StreamingAssets（Android）。E7Build 出 APK 前也走这一步。</summary>
    public static BuildResult BuildAndroidAb()
    {
        EnsureCollector();
        return Build(BuildTarget.Android, AssetBundleBuilderHelper.GetStreamingAssetsRoot());
    }

    static void EnsureCollector()
    {
        var packages = AssetBundleCollectorSettingData.Setting.Packages;
        for (var i = packages.Count - 1; i >= 0; i--)
        {
            if (packages[i].PackageName == PackageName)
            {
                AssetBundleCollectorSettingData.RemovePackage(packages[i]);
            }
        }

        var package = AssetBundleCollectorSettingData.CreatePackage(PackageName);
        // P1-3a1 起还是这一个包、这一个组、这三处收集路径：
        // 八块地表是 Assets/Art/Launch 的子目录，被 marker 这条 PackDirectory 一起收进来，
        // 不另开 AB、不另起打包流程。
        package.PackageDesc = "炼体到结丹：marker、八块地表、Boss 场景、热更 dll";
        package.EnableAddressable = true;
        package.AutoCollectShaders = false;
        var group = AssetBundleCollectorSettingData.CreateGroup(package, PackageName);
        group.GroupDesc = "炼体到结丹";

        // 这一条收整个 Assets/Art/Launch，Region/ 子目录里的八块地表随之进同一组 Launch。
        var marker = new AssetBundleCollector();
        marker.CollectPath = "Assets/Art/Launch";
        marker.CollectorGUID = AssetDatabase.AssetPathToGUID(marker.CollectPath);
        marker.CollectorType = ECollectorType.MainAssetCollector;
        marker.AddressRuleName = nameof(AddressByFileName);
        marker.PackRuleName = nameof(PackDirectory);
        marker.FilterRuleName = nameof(CollectAll);
        AssetBundleCollectorSettingData.CreateCollector(group, marker);

        // P0-2a：Boss 场景进 Launch，地址就是文件名 BossBattle。
        var scene = new AssetBundleCollector();
        scene.CollectPath = "Assets/Scenes/BossBattle.unity";
        scene.CollectorGUID = AssetDatabase.AssetPathToGUID(scene.CollectPath);
        scene.CollectorType = ECollectorType.MainAssetCollector;
        scene.AddressRuleName = nameof(AddressByFileName);
        scene.PackRuleName = nameof(PackDirectory);
        scene.FilterRuleName = nameof(CollectAll);
        AssetBundleCollectorSettingData.CreateCollector(group, scene);

        // 热更 dll 导入为 TextAsset 进 AssetBundle。不走原生文件：YooAsset 的内置
        // 文件系统在 Android 上读不了 RawFile bundle，TextAsset 一切平台都支持。
        var dll = new AssetBundleCollector();
        dll.CollectPath = HotDllDir;
        dll.CollectorGUID = AssetDatabase.AssetPathToGUID(dll.CollectPath);
        dll.CollectorType = ECollectorType.MainAssetCollector;
        dll.AddressRuleName = nameof(AddressByFileName);
        dll.PackRuleName = nameof(PackDirectory);
        dll.FilterRuleName = nameof(CollectAll);
        AssetBundleCollectorSettingData.CreateCollector(group, dll);

        AssetBundleCollectorSettingData.SaveFile();
    }

    static BuildResult Build(BuildTarget target, string buildinRoot)
    {
        var parameters = new BuiltinBuildParameters();
        parameters.BuildOutputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot();
        parameters.BuildinFileRoot = buildinRoot;
        parameters.BuildPipeline = EBuildPipeline.BuiltinBuildPipeline.ToString();
        parameters.BuildBundleType = (int)YooAsset.EBuildBundleType.AssetBundle;
        parameters.BuildTarget = target;
        parameters.PackageName = PackageName;
        parameters.PackageVersion = PackageVersion;
        parameters.VerifyBuildingResult = true;
        parameters.EnableSharePackRule = true;
        parameters.FileNameStyle = YooAsset.EFileNameStyle.HashName;
        parameters.BuildinFileCopyOption = EBuildinFileCopyOption.ClearAndCopyAll;
        parameters.BuildinFileCopyParams = string.Empty;
        parameters.CompressOption = ECompressOption.LZ4;
        parameters.ClearBuildCacheFiles = true;
        parameters.UseAssetDependencyDB = true;
        return new BuiltinBuildPipeline().Run(parameters, true);
    }

    /// <summary>八块首发地表的资源路径都在同一份 Launch manifest 里。</summary>
    public static bool AllRegionsIn(string manifest)
    {
        // YooAsset 的 manifest 可能把中文写成 \uXXXX，两种写法都要认。
        var text = DecodeUnicode(manifest);
        foreach (var name in AOT.LaunchSession.RegionNames)
        {
            if (!text.Contains(AOT.LaunchSession.RegionRoot + "/" + name + ".txt"))
            {
                return false;
            }
        }

        return true;
    }

    public static string DecodeUnicode(string text) =>
        Regex.Replace(text, @"\\u([0-9a-fA-F]{4})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    static bool HasLaterRealm(string text)
    {
        string[] realms = { "元婴", "化神", "返虚", "合体", "渡劫", "大乘", "金仙", "散仙", "极境" };
        foreach (var realm in realms)
        {
            if (text.Contains(realm))
            {
                return true;
            }
        }

        return false;
    }
}

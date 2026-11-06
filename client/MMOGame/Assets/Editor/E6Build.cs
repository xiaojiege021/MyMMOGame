using System.IO;
using UnityEditor;
using UnityEngine;
using YooAsset.Editor;

public static class E6Build
{
    const string PackageName = "Launch";
    const string PackageVersion = "1";

    public static void SelfCheck()
    {
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

        var android = Build(BuildTarget.Android, AssetBundleBuilderHelper.GetStreamingAssetsRoot());
        if (!android.Success)
        {
            Debug.Log("launch_check=fail build_android=" + android.ErrorInfo);
            EditorApplication.Exit(1);
            return;
        }

        var manifestPath = Path.Combine(android.OutputPackageDirectory, PackageName + "_" + PackageVersion + ".json");
        var manifest = File.ReadAllText(manifestPath);
        if (!manifest.Contains(AOT.LaunchSession.MarkerPath) || HasLaterRealm(manifest))
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
        package.PackageDesc = "炼体到结丹占位";
        package.EnableAddressable = true;
        package.AutoCollectShaders = false;
        var group = AssetBundleCollectorSettingData.CreateGroup(package, PackageName);
        group.GroupDesc = "炼体到结丹占位";
        var collector = new AssetBundleCollector();
        collector.CollectPath = "Assets/Art/Launch";
        collector.CollectorGUID = AssetDatabase.AssetPathToGUID(collector.CollectPath);
        collector.CollectorType = ECollectorType.MainAssetCollector;
        collector.AddressRuleName = nameof(AddressByFileName);
        collector.PackRuleName = nameof(PackDirectory);
        collector.FilterRuleName = nameof(CollectAll);
        AssetBundleCollectorSettingData.CreateCollector(group, collector);
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

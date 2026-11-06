using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using YooAsset;

namespace AOT
{
    public static class LaunchSession
    {
        public const string PackageName = "Launch";
        public const string MarkerAddress = "marker";
        public const string MarkerText = "炼体到结丹";
        public const string MarkerPath = "Assets/Art/Launch/marker.txt";

        static readonly string[] LaterRealms =
        {
            "元婴", "化神", "返虚", "合体", "渡劫", "大乘", "金仙", "散仙", "极境"
        };

        public static IEnumerator Run(string packageRoot, Action<bool, string> finished)
        {
            YooAssets.Initialize();
            var package = YooAssets.TryGetPackage(PackageName);
            if (package == null)
            {
                package = YooAssets.CreatePackage(PackageName);
            }

            YooAssets.SetDefaultPackage(package);
            var fileSystem = FileSystemParameters.CreateDefaultBuildinFileSystemParameters(null, packageRoot);
            var parameters = new OfflinePlayModeParameters();
            parameters.BuildinFileSystemParameters = fileSystem;
            var init = package.InitializeAsync(parameters);
            yield return init;
            if (init.Status != EOperationStatus.Succeed)
            {
                finished(false, "init=" + init.Error);
                yield break;
            }

            var version = package.RequestPackageVersionAsync(false);
            yield return version;
            if (version.Status != EOperationStatus.Succeed)
            {
                finished(false, "version=" + version.Error);
                yield break;
            }

            var manifest = package.UpdatePackageManifestAsync(version.PackageVersion);
            yield return manifest;
            if (manifest.Status != EOperationStatus.Succeed)
            {
                finished(false, "manifest=" + manifest.Error);
                yield break;
            }

            var paths = new List<string>();
            foreach (var info in package.GetAllAssetInfos())
            {
                paths.Add(info.AssetPath);
            }

            paths.Sort();
            if (!OnlyPlaceholder(paths, out var pathDetail))
            {
                finished(false, pathDetail);
                yield break;
            }

            var handle = package.LoadAssetAsync<TextAsset>(MarkerAddress);
            yield return handle;
            if (handle.Status != EOperationStatus.Succeed)
            {
                finished(false, "load=" + handle.LastError);
                yield break;
            }

            var text = handle.GetAssetObject<TextAsset>().text.Trim();
            handle.Release();
            if (text != MarkerText)
            {
                finished(false, "text=" + text);
                yield break;
            }

            finished(true, "version=" + version.PackageVersion + " loaded=" + text + " assets=" + string.Join(",", paths));
        }

        public static bool OnlyPlaceholder(List<string> paths, out string detail)
        {
            if (paths.Count != 1 || paths[0] != MarkerPath)
            {
                detail = "assets=" + string.Join(",", paths);
                return false;
            }

            foreach (var realm in LaterRealms)
            {
                if (paths[0].Contains(realm))
                {
                    detail = "later=" + realm;
                    return false;
                }
            }

            detail = string.Empty;
            return true;
        }
    }
}

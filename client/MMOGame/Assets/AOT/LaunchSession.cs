using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using YooAsset;

namespace AOT
{
    public static class LaunchSession
    {
        public const string PackageName = "Launch";
        public const string MarkerAddress = "marker";
        public const string MarkerText = "炼体到结丹";
        public const string MarkerPath = "Assets/Art/Launch/marker.txt";
        public const string SceneAddress = "BossBattle";
        public const string HotUpdateAddress = "HotUpdate";
        public const string HotUpdateEntryType = "HotUpdate.Entry";

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
            if (!CheckAssets(paths, out var pathDetail))
            {
                finished(false, pathDetail);
                yield break;
            }

            var marker = package.LoadAssetAsync<TextAsset>(MarkerAddress);
            yield return marker;
            if (marker.Status != EOperationStatus.Succeed)
            {
                finished(false, "load=" + marker.LastError);
                yield break;
            }

            var text = marker.GetAssetObject<TextAsset>().text.Trim();
            marker.Release();
            if (text != MarkerText)
            {
                finished(false, "text=" + text);
                yield break;
            }

            // 热更程序集是包里的 TextAsset。先加载它，再加载场景——
            // 场景里挂了热更脚本，程序集不在的话组件会整个丢掉。
            // 不用原生文件 bundle：内置文件系统在 Android 上读不了 RawFile。
            var dll = package.LoadAssetAsync<TextAsset>(HotUpdateAddress);
            yield return dll;
            if (dll.Status != EOperationStatus.Succeed)
            {
                finished(false, "dll=" + dll.LastError);
                yield break;
            }

            Assembly hotAssembly;
            try
            {
                hotAssembly = Assembly.Load(dll.GetAssetObject<TextAsset>().bytes);
            }
            catch (Exception e)
            {
                finished(false, "asm=" + e.GetType().Name + " " + e.Message);
                yield break;
            }

            var entry = hotAssembly.GetType(HotUpdateEntryType);
            if (entry == null)
            {
                finished(false, "no_type=" + HotUpdateEntryType);
                yield break;
            }

            var ping = entry.GetMethod("Ping", BindingFlags.Public | BindingFlags.Static);
            if (ping == null || !"hotupdate-ok".Equals(ping.Invoke(null, null)))
            {
                finished(false, "entry_ping=fail");
                yield break;
            }

            // additive：Empty 场景上的协程不被拆掉，Boss 场景带着自己的相机叠上来。
            var scene = package.LoadSceneAsync(SceneAddress, LoadSceneMode.Additive);
            yield return scene;
            if (scene.Status != EOperationStatus.Succeed)
            {
                finished(false, "scene=" + scene.LastError);
                yield break;
            }

            finished(true, "version=" + version.PackageVersion
                + " marker=" + text
                + " hotupdate=v" + hotAssembly.GetName().Version
                + " scene=" + SceneAddress
                + " assets=" + string.Join(",", paths));
        }

        /// <summary>
        /// 包内容检查。P0-2a 起 Launch 不再是纯占位：marker、Boss 场景、热更 dll
        /// 三样必须在；后期境界的资源不许提前混进来。
        /// </summary>
        public static bool CheckAssets(List<string> paths, out string detail)
        {
            detail = string.Empty;
            bool hasMarker = false, hasScene = false, hasDll = false;
            foreach (var path in paths)
            {
                if (path == MarkerPath)
                {
                    hasMarker = true;
                }

                if (path.Contains(SceneAddress))
                {
                    hasScene = true;
                }

                if (path.EndsWith(HotUpdateAddress + ".bytes") || path.Contains("HotUpdateDlls"))
                {
                    hasDll = true;
                }
            }

            if (!hasMarker || !hasScene || !hasDll)
            {
                detail = "assets=" + string.Join(",", paths)
                    + " marker=" + hasMarker + " scene=" + hasScene + " dll=" + hasDll;
                return false;
            }

            foreach (var realm in LaterRealms)
            {
                foreach (var path in paths)
                {
                    if (path.Contains(realm))
                    {
                        detail = "later=" + realm;
                        return false;
                    }
                }
            }

            return true;
        }
    }
}

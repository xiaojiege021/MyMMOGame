using System;
using System.IO;
using HybridCLR.Installer;
using UnityEditor;
using UnityEngine;

// E7 阶段：装 HybridCLR 的 IL2CPP 补丁。
// 走官方 InstallerController.InstallFromLocal，来源目录由环境变量 MMO_IL2CPP_SRC 指定，
// 该目录下必须有 libil2cpp（il2cpp_plus 的，且已放入 hybridclr）。
public static class E7Install
{
    public static void Run()
    {
        var src = Environment.GetEnvironmentVariable("MMO_IL2CPP_SRC");
        if (string.IsNullOrEmpty(src))
        {
            Debug.Log("patch_install=fail reason=MMO_IL2CPP_SRC 没有设置");
            EditorApplication.Exit(1);
            return;
        }

        var libil2cpp = Path.Combine(src, "libil2cpp");
        if (!Directory.Exists(Path.Combine(libil2cpp, "hybridclr")))
        {
            Debug.Log("patch_install=fail reason=" + libil2cpp + " 下没有 hybridclr");
            EditorApplication.Exit(1);
            return;
        }

        var installer = new InstallerController();
        Debug.Log("patch_expect=package:" + installer.PackageVersion
            + " hybridclr:" + installer.HybridclrLocalVersion
            + " il2cpp_plus:" + installer.Il2cppPlusLocalVersion
            + " before:" + (installer.InstalledLibil2cppVersion ?? "none"));

        installer.InstallFromLocal(libil2cpp);

        var after = installer.InstalledLibil2cppVersion;
        var hasHybridclr = installer.HasInstalledHybridCLR();
        var versionFile = new FileInfo(installer.LocalVersionFile);
        Debug.Log("patch_install=" + (hasHybridclr && after == installer.PackageVersion ? "ok" : "fail")
            + " version=" + (after ?? "none")
            + " hybridclr=" + (hasHybridclr ? "present" : "missing")
            + " il2cpp_dir=" + HybridCLR.SettingsUtil.LocalIl2CppDir
            + " size_mb=" + (versionFile.Exists ? DirSizeMb(HybridCLR.SettingsUtil.LocalIl2CppDir) : 0));
        EditorApplication.Exit(hasHybridclr && after == installer.PackageVersion ? 0 : 1);
    }

    static long DirSizeMb(string dir)
    {
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(f).Length;
        }
        return total / (1024 * 1024);
    }
}

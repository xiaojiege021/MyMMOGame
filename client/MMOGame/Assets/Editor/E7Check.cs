using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HybridCLR.Commands;
using HybridCLR.Settings;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

// E7 核对用：
//   1. 热更程序集登记进 HybridCLR 设置
//   2. 热更目录里不能有伤害结算
//   3. 用 HybridCLR 的命令编译出热更 dll
//   4. 把 dll 当字节流加载，反射到 HotUpdate.Entry
public static class E7Check
{
    const string HotUpdateDir = "Assets/HotUpdate";
    const string HotUpdateAsmdef = "Assets/HotUpdate/HotUpdate.asmdef";
    const string HotUpdateDll = "HotUpdate.dll";

    // 热更侧可以读回包里的结算号（客户端领奖要用），但不能自己写结算：
    // 落盘、建目录、写库都算越界。加减血量和伤害同样不行。
    static readonly string[] Forbidden =
    {
        "扣血", "SetHp", "Npgsql", "StreamWriter", "File.WriteAll", "File.AppendAll", "Directory.CreateDirectory",
    };

    // 光看「Damage」这个词会把「显示回包里的伤害」也算成违规——热更侧本来就要把
    // 服务端回的伤害摆到界面上。真正不该出现的是自己加减：Hp -= 或 damage +=。
    // 和 check-p0-1c.sh 第 7 段的口径相同，那边查 Net 与 HotUpdate 两处。
    static readonly System.Text.RegularExpressions.Regex[] ForbiddenPatterns =
    {
        // Hp -= ...、MaxHp += ... 这类。
        new(@"\b\w*[Hh]p\s*[-+]="),
        // 变量名到 damage 为止，后面直接就是运算符。放过 _damageAge += 这种计时器。
        new(@"\b\w*[Dd]amage\s*[-+]="),
    };

    public static void Run()
    {
        try
        {
            Register();
            if (!NoDamageSettlement())
            {
                Fail("damage_in_hotupdate");
                return;
            }

            var dir = Path.Combine(Path.GetTempPath(), "mmo-e7-dlls");
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            var target = EditorUserBuildSettings.activeBuildTarget;
            CompileDllCommand.CompileDll(dir, target, false);

            var dll = Path.Combine(dir, HotUpdateDll);
            if (!File.Exists(dll))
            {
                Fail("missing_dll=" + dll);
                return;
            }

            var bytes = File.ReadAllBytes(dll);
            if (!LoadAndProbe(bytes, out var mode, out var detail))
            {
                Fail(detail);
                return;
            }

            Debug.Log("hotupdate=ok " + detail + " target=" + target + " mode=" + mode + " dll=" + dll);
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Fail("exception=" + e.GetType().Name + " " + e.Message);
        }
    }

    static void Register()
    {
        var settings = HybridCLRSettings.instance;
        settings.enable = true;
        var asmdef = AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(HotUpdateAsmdef);
        if (asmdef == null)
        {
            throw new Exception("找不到 " + HotUpdateAsmdef);
        }

        // 两条登记通道只能二选一：asmdef 和程序集名同时登记，打包会报
        // hot update assembly:HotUpdate is duplicated。这里只留 asmdef，名字那条清空。
        settings.hotUpdateAssemblyDefinitions = new[] { asmdef };
        settings.hotUpdateAssemblies = Array.Empty<string>();
        HybridCLRSettings.Save();
        Debug.Log("hotupdate_registered=" + asmdef.name);
    }

    static bool NoDamageSettlement()
    {
        foreach (var file in Directory.GetFiles(HotUpdateDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = StripComments(File.ReadAllText(file));
            foreach (var word in Forbidden)
            {
                if (text.Contains(word))
                {
                    Debug.Log("hotupdate_forbidden=" + word + " file=" + file);
                    return false;
                }
            }

            foreach (var pattern in ForbiddenPatterns)
            {
                var match = pattern.Match(text);
                if (match.Success)
                {
                    Debug.Log("hotupdate_forbidden=" + match.Value.Trim() + " file=" + file);
                    return false;
                }
            }
        }

        return true;
    }

    // 注释里提一句「结算不在客户端算」只是说明分工，不代表热更里真在算。
    // 先剥掉注释再查词，否则一句注释就能把整条检查顶掉；正常代码仍照查。
    static string StripComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        var i = 0;
        while (i < src.Length)
        {
            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n')
                {
                    i++;
                }
            }
            else if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/'))
                {
                    i++;
                }
                i = Math.Min(i + 2, src.Length);
            }
            else if (src[i] == '"' || src[i] == '\'')
            {
                var quote = src[i];
                sb.Append(src[i]);
                i++;
                while (i < src.Length)
                {
                    sb.Append(src[i]);
                    if (src[i] == '\\' && i + 1 < src.Length)
                    {
                        sb.Append(src[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (src[i] == quote)
                    {
                        i++;
                        break;
                    }

                    i++;
                }
            }
            else
            {
                sb.Append(src[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    // 编辑器里同名程序集已经加载过，直接 Assembly.Load 会撞名。
    // 撞名时另起一个域再加载，确认这份 dll 本身能装进运行时。
    static bool LoadAndProbe(byte[] bytes, out string mode, out string detail)
    {
        mode = "current";
        try
        {
            detail = Probe(Assembly.Load(bytes));
            return true;
        }
        catch (FileLoadException)
        {
            mode = "domain";
        }
        catch (ArgumentException)
        {
            mode = "domain";
        }

        AppDomain domain = null;
        try
        {
            domain = AppDomain.CreateDomain("e7-probe");
            var loaded = Assembly.Load(bytes);
            detail = Probe(loaded);
            return true;
        }
        catch (Exception e)
        {
            detail = "load=" + e.GetType().Name + " " + e.Message;
            return false;
        }
        finally
        {
            if (domain != null)
            {
                AppDomain.Unload(domain);
            }
        }
    }

    static string Probe(Assembly assembly)
    {
        var type = assembly.GetType("HotUpdate.Entry");
        if (type == null)
        {
            throw new Exception("no_type=HotUpdate.Entry");
        }

        var version = type.GetField("Version", BindingFlags.Public | BindingFlags.Static);
        var ping = type.GetMethod("Ping", BindingFlags.Public | BindingFlags.Static);
        var empty = type.GetMethod("EmptyBytes", BindingFlags.Public | BindingFlags.Static);

        var types = new List<string>();
        foreach (var t in assembly.GetTypes())
        {
            if (t.Name.Contains("Damage"))
            {
                types.Add(t.Name);
            }
        }

        if (types.Count > 0)
        {
            throw new Exception("damage_type=" + string.Join(",", types));
        }

        return "asm=" + assembly.GetName().Name
            + " version=" + version.GetValue(null)
            + " ping=" + ping.Invoke(null, null)
            + " empty_bytes=" + empty.Invoke(null, null)
            + " types=" + assembly.GetTypes().Length;
    }

    static void Fail(string detail)
    {
        Debug.Log("hotupdate=fail " + detail);
        EditorApplication.Exit(1);
    }
}

using System;
using System.IO;
using System.Reflection;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace AOT
{
    // 启动时加载热更程序集。这里只用反射，AOT 侧不直接引用热更类型。
    // 热更包里只放界面和表现，伤害与发奖不在这里结算。
    public static class HotUpdateLoader
    {
        public const string RequestFile = "Temp/e7-hotupdate.txt";
        public const string HotUpdateAssembly = "HotUpdate.dll";
        public const string EntryType = "HotUpdate.Entry";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Start()
        {
            if (!File.Exists(RequestFile))
            {
                return;
            }

            var dir = File.ReadAllText(RequestFile).Trim();
            File.Delete(RequestFile);
            if (dir.Length == 0)
            {
                return;
            }

            var ok = Load(dir, out var detail);
            Debug.Log(ok ? "hotupdate=ok " + detail : "hotupdate=fail " + detail);
#if UNITY_EDITOR
            EditorApplication.Exit(ok ? 0 : 1);
#endif
        }

        public static bool Load(string dir, out string detail)
        {
            var path = Path.Combine(dir, HotUpdateAssembly);
            if (!File.Exists(path))
            {
                detail = "missing=" + path;
                return false;
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                var assembly = Assembly.Load(bytes);
                var type = assembly.GetType(EntryType);
                if (type == null)
                {
                    detail = "no_type=" + EntryType;
                    return false;
                }

                var ping = type.GetMethod("Ping", BindingFlags.Public | BindingFlags.Static);
                var empty = type.GetMethod("EmptyBytes", BindingFlags.Public | BindingFlags.Static);
                var version = type.GetField("Version", BindingFlags.Public | BindingFlags.Static);
                if (ping == null || empty == null || version == null)
                {
                    detail = "no_entry_members";
                    return false;
                }

                detail = "asm=" + assembly.GetName().Name
                    + " version=" + version.GetValue(null)
                    + " ping=" + ping.Invoke(null, null)
                    + " empty_bytes=" + empty.Invoke(null, null)
                    + " bytes=" + bytes.Length;
                return true;
            }
            catch (Exception e)
            {
                detail = "load=" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }
    }
}

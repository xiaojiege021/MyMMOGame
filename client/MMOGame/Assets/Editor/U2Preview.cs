using System;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// U2 肉眼验收入口：播放模式下从菜单直接开四屏里的任意一屏。
// 与 U1 一样走反射——`HotUpdate` 是 autoReferenced:false，Editor 程序集引不到它。
// 这里只负责把屏打开，不填账号、不造令牌：口令与账号要在屏上自己敲。
public static class U2Preview
{
    [MenuItem("Tools/U2 打开登入屏")]
    public static void OpenLogin()
    {
        Open("OpenLogin");
    }

    [MenuItem("Tools/U2 打开立身屏")]
    public static void OpenCreate()
    {
        Open("OpenCreate");
    }

    [MenuItem("Tools/U2 打开角色栏")]
    public static void OpenRoster()
    {
        Open("OpenRoster");
    }

    [MenuItem("Tools/U2 打开载入屏")]
    public static void OpenLoading()
    {
        Open("OpenLoading");
    }

    static void Open(string method)
    {
        if (!Application.isPlaying)
        {
            Debug.Log("u2_preview=need_play " + method);
            return;
        }

        try
        {
            var shellType = FindType("HotUpdate.Ui.UiShell");
            var flowType = FindType("HotUpdate.Ui.UiEntryFlow");
            if (shellType == null || flowType == null)
            {
                Debug.Log("u2_preview=fail type_missing");
                return;
            }

            var shell = shellType.GetMethod("Ensure").Invoke(null, null);
            var flow = flowType.GetMethod("Ensure").Invoke(null, new[] { shell });
            flowType.GetMethod(method).Invoke(flow, null);
            Debug.Log("u2_preview=ok " + method);
        }
        catch (Exception error)
        {
            var inner = error.InnerException ?? error;
            Debug.LogError("u2_preview=fail " + inner.GetType().Name + ":" + inner.Message);
        }
    }

    static Type FindType(string name)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (var i = 0; i < assemblies.Length; i++)
        {
            var type = assemblies[i].GetType(name);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }
}

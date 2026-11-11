using System;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// U4 肉眼验收入口：播放模式下从菜单直接开五屏（B01–B05）。
// 与 U1、U2、U3 一样走反射——`HotUpdate` 是 autoReferenced:false，Editor 程序集引不到它。
// 这里只负责把屏打开，不填令牌、不造回包：屏上显示的就是「还没进分线 / 还没采集」的样子。
public static class U4Preview
{
    [MenuItem("Tools/U4 打开大世界 HUD")]
    public static void OpenHud()
    {
        Open("hud");
    }

    [MenuItem("Tools/U4 打开世界地图与跨区")]
    public static void OpenMap()
    {
        Open("map");
    }

    [MenuItem("Tools/U4 打开 NPC 对白")]
    public static void OpenDialogue()
    {
        Open("dialogue");
    }

    [MenuItem("Tools/U4 打开采集节点")]
    public static void OpenGather()
    {
        Open("gather");
    }

    [MenuItem("Tools/U4 打开飞行与境界锁")]
    public static void OpenFly()
    {
        Open("fly");
    }

    static void Open(string which)
    {
        if (!Application.isPlaying)
        {
            Debug.Log("u4_preview=need_play " + which);
            return;
        }

        try
        {
            var type = FindType("HotUpdate.Ui.UiWorldFlow");
            if (type == null)
            {
                Debug.Log("u4_preview=fail type_missing");
                return;
            }

            type.GetMethod("Open").Invoke(null, new object[] { which });
        }
        catch (Exception error)
        {
            var inner = error.InnerException ?? error;
            Debug.LogError("u4_preview=fail " + inner.GetType().Name + ":" + inner.Message);
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

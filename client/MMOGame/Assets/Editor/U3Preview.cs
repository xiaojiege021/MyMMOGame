using System;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// U3 肉眼验收入口：播放模式下从菜单直接开三屏（C01 战斗 HUD 在战斗场景里，跟着 P0-3c 看）。
// 与 U1、U2 一样走反射——`HotUpdate` 是 autoReferenced:false，Editor 程序集引不到它。
// 这里只负责把屏打开，不填令牌、不造回包：屏上显示的就是「还没进本 / 还没结算」的样子。
public static class U3Preview
{
    [MenuItem("Tools/U3 打开裂隙·副本入口")]
    public static void OpenDungeon()
    {
        Open("dungeon");
    }

    [MenuItem("Tools/U3 打开战斗结算")]
    public static void OpenSettle()
    {
        Open("settle");
    }

    [MenuItem("Tools/U3 打开小队")]
    public static void OpenParty()
    {
        Open("party");
    }

    static void Open(string which)
    {
        if (!Application.isPlaying)
        {
            Debug.Log("u3_preview=need_play " + which);
            return;
        }

        try
        {
            var type = FindType("HotUpdate.Ui.UiDungeonFlow");
            if (type == null)
            {
                Debug.Log("u3_preview=fail type_missing");
                return;
            }

            type.GetMethod("Open").Invoke(null, new object[] { which });
        }
        catch (Exception error)
        {
            var inner = error.InnerException ?? error;
            Debug.LogError("u3_preview=fail " + inner.GetType().Name + ":" + inner.Message);
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

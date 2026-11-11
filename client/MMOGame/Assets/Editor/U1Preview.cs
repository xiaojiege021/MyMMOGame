using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// U1 骨架预览：在编辑器 Play 模式下打开通用状态屏，肉眼确认两套皮肤。
// HotUpdate 程序集 autoReferenced:false，这里引不到它的类型，用反射调。
// 不参与核对，也不进包。
public static class U1Preview
{
    [MenuItem("Tools/U1 打开通用状态屏")]
    public static void Open()
    {
        if (!Application.isPlaying)
        {
            Debug.Log("u1_preview=need_playmode 先按 Play 再点这个菜单");
            return;
        }

        var shellType = FindType("HotUpdate.Ui.UiShell");
        if (shellType == null)
        {
            Debug.Log("u1_preview=type_missing");
            return;
        }

        var ensure = shellType.GetMethod("Ensure", BindingFlags.Public | BindingFlags.Static);
        var open = shellType.GetMethod("OpenStatus", BindingFlags.Public | BindingFlags.Instance);
        if (ensure == null || open == null)
        {
            Debug.Log("u1_preview=method_missing");
            return;
        }

        var shell = ensure.Invoke(null, null);
        open.Invoke(shell, null);
        Debug.Log("u1_preview=open");
    }

    [MenuItem("Tools/U1 切换皮肤（明亮 / 暗黑）")]
    public static void Toggle()
    {
        if (!Application.isPlaying)
        {
            Debug.Log("u1_preview=need_playmode 先按 Play 再点这个菜单");
            return;
        }

        var shellType = FindType("HotUpdate.Ui.UiShell");
        if (shellType == null)
        {
            Debug.Log("u1_preview=type_missing");
            return;
        }

        var ensure = shellType.GetMethod("Ensure", BindingFlags.Public | BindingFlags.Static);
        var toggle = shellType.GetMethod("ToggleSkin", BindingFlags.Public | BindingFlags.Instance);
        if (ensure == null || toggle == null)
        {
            Debug.Log("u1_preview=method_missing");
            return;
        }

        var shell = ensure.Invoke(null, null);
        toggle.Invoke(shell, null);
        Debug.Log("u1_preview=toggle");
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

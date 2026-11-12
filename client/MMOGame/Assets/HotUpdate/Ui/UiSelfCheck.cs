#if UNITY_EDITOR
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U1 骨架自检。因为 <c>HotUpdate</c> 程序集 <c>autoReferenced: false</c>，
    /// <c>Assets/Editor</c> 里的脚本引不到这里的类型，所以实例断言放在本程序集，
    /// 由编辑器入口用反射调 <see cref="Run"/>。
    ///
    /// 只在编辑器编译，不进包。验的都是「切皮肤只换颜色」这条写定口径：
    /// 布局与文案必须一模一样，颜色必须变，偏好必须落在本机。
    /// </summary>
    public static class UiSelfCheck
    {
        static bool _failed;

        /// <summary>跑一遍。返回是否全过。</summary>
        public static bool Run()
        {
            _failed = false;

            // 从干净的偏好开始，免得上一轮的选择影响「默认明亮」这条。
            UiSkinStore.Reset();
            var shell = UiShell.Ensure();

            var defaultOk = shell != null && shell.IsReady && shell.Skin == UiSkin.Light
                            && UiSkinStore.Load() == UiSkin.Light;
            Report("default", defaultOk);
            if (shell == null)
            {
                return false;
            }

            shell.OpenStatus();
            var lightLayout = LayoutSnapshot(shell);
            var lightText = TextSnapshot(shell);
            var lightColor = ColorSnapshot(shell);
            var widgets = shell.GetComponentsInChildren<RectTransform>(true).Length;
            Report("page", widgets > 40 && UiReasonCatalog.Count == 36);
            Debug.Log("u1_self_widgets=" + widgets + " reasons=" + UiReasonCatalog.Count);

            shell.SetSkin(UiSkin.Dark, false);
            var darkLayout = LayoutSnapshot(shell);
            var darkText = TextSnapshot(shell);
            var darkColor = ColorSnapshot(shell);

            Report("layout", lightLayout == darkLayout);
            Report("copy", lightText == darkText);
            Report("color", lightColor != darkColor);

            // 偏好必须落在本机：切一次就能读回来，清掉就回默认。
            shell.SetSkin(UiSkin.Dark, true);
            var saved = UiSkinStore.Load() == UiSkin.Dark;

            // 「恢复默认」= 清偏好 + 按偏好切回明亮，两件事都要成立。
            UiSkinStore.Reset();
            shell.SetSkin(UiSkinStore.Load(), false);
            var reset = UiSkinStore.Load() == UiSkin.Light && shell.Skin == UiSkin.Light;
            Report("persist", saved);
            Report("reset", reset);

            // 命中：按画出来的按钮矩形点进去要命中，左下角空白处不该命中。
            var hitButton = false;
            var rects = shell.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < rects.Length; i++)
            {
                if (rects[i].name != "Button")
                {
                    continue;
                }

                // 布局是画布单位，点选接口吃屏幕像素：先换算再点。
                var center = shell.ToScreen(rects[i].anchoredPosition + rects[i].sizeDelta * 0.5f);
                if (shell.HitTest(center))
                {
                    hitButton = true;
                }
            }

            Report("hit", hitButton && !shell.HitTest(shell.ToScreen(new Vector2(2f, 2f))));

            shell.ClosePage();
            UiSkinStore.Reset();
            Object.DestroyImmediate(shell.gameObject);

            Debug.Log("u1_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        static void Report(string key, bool ok)
        {
            Debug.Log("u1_self_" + key + "=" + (ok ? "ok" : "fail"));
            if (!ok)
            {
                _failed = true;
            }
        }

        /// <summary>所有矩形的位置与尺寸。切皮肤不许动这些。</summary>
        static string LayoutSnapshot(UiShell shell)
        {
            var builder = new StringBuilder();
            var rects = shell.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < rects.Length; i++)
            {
                var rt = rects[i];
                builder.Append(rt.name).Append(':')
                    .Append(rt.anchoredPosition.x.ToString("0.##")).Append(',')
                    .Append(rt.anchoredPosition.y.ToString("0.##")).Append(',')
                    .Append(rt.sizeDelta.x.ToString("0.##")).Append(',')
                    .Append(rt.sizeDelta.y.ToString("0.##")).Append(';');
            }

            return builder.ToString();
        }

        /// <summary>所有文案。切皮肤不许改文案。</summary>
        static string TextSnapshot(UiShell shell)
        {
            var builder = new StringBuilder();
            var labels = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                builder.Append(labels[i].name).Append(':').Append(labels[i].text).Append(';');
            }

            return builder.ToString();
        }

        /// <summary>所有画出来的颜色。切皮肤必须变。</summary>
        static string ColorSnapshot(UiShell shell)
        {
            var builder = new StringBuilder();
            var images = shell.GetComponentsInChildren<Image>(true);
            for (var i = 0; i < images.Length; i++)
            {
                Append(builder, images[i].color);
            }

            var labels = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                Append(builder, labels[i].color);
            }

            return builder.ToString();
        }

        static void Append(StringBuilder builder, Color color)
        {
            builder.Append(color.r.ToString("0.###")).Append(',')
                .Append(color.g.ToString("0.###")).Append(',')
                .Append(color.b.ToString("0.###")).Append(',')
                .Append(color.a.ToString("0.###")).Append(';');
        }
    }
}
#endif

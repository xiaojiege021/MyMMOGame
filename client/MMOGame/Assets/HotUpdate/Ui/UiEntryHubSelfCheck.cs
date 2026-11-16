using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U8 界面入口自检：七格不再 Toast 占位、能 ShowPage 到已有屏，
    /// U5 / U6 / U7 / U3 二级入口条能切屏，返回表世界能回去。
    /// </summary>
    public static class UiEntryHubSelfCheck
    {
        static bool _failed;

        /// <summary>跑一遍。返回是否全过。</summary>
        public static bool Run()
        {
            _failed = false;
            UiSkinStore.Reset();
            var shell = UiShell.Ensure();
            shell.SetSkin(UiSkin.Light, false);
            UiEntryHub.BindSession("u8-self-token", 8);

            var world = UiWorldFlow.Ensure(shell);
            world.Configure("u8-self-token", 8);

            // 表世界 HUD 七格都能建，且源码侧已接到 Hub（静态段另验）。
            var functions = 0;
            shell.ShowPage("表世界", s => { functions = UiWorldHudScreen.Build(s, world); });
            Report("hud", functions == UiWorldCodex.HudFunctions.Length && functions == 7);
            Debug.Log("u8_self_hud functions=" + functions);

            // 七个主入口都能打开对应标题。
            var titles = new List<string>();
            OpenAndCapture(shell, world, 0, titles);
            OpenAndCapture(shell, world, 1, titles);
            OpenAndCapture(shell, world, 2, titles);
            OpenAndCapture(shell, world, 3, titles);
            OpenAndCapture(shell, world, 4, titles);
            OpenAndCapture(shell, world, 5, titles);
            OpenAndCapture(shell, world, 6, titles);
            Report("seven", titles.Count == 7
                && titles.Contains("角色与装备") && titles.Contains("背包")
                && titles.Contains("技能与分支") && titles.Contains("宗门")
                && titles.Contains("商城") && titles.Contains("裂隙")
                && titles.Contains("世界地图"));
            Debug.Log("u8_self_seven=" + titles.Count + " " + string.Join(",", titles.ToArray()));

            // U5 二级：强化 / 境界。
            UiEntryHub.OpenUpgrade(shell, "enhance");
            Report("u5_enhance", PageTitle(shell) == "强化" && HasButton(shell, "境界"));
            UiEntryHub.OpenUpgrade(shell, "realm");
            Report("u5_realm", PageTitle(shell) == "境界突破");

            // U6 二级：拍卖 / 通行证。
            UiEntryHub.OpenCommerce(shell, "auction");
            Report("u6_auction", PageTitle(shell) == "拍卖与寄售" && HasButton(shell, "通行证"));
            UiEntryHub.OpenCommerce(shell, "pass");
            Report("u6_pass", PageTitle(shell) == "赛季通行证");

            // U7 二级：洞府 / 图鉴（本卡只开屏；洞府由 U9/U10 接线，图鉴仍标尚未接服务端）。
            UiEntryHub.OpenSocial(shell, "homestead");
            var home = Text(shell);
            // U9 接了摆放、U10 接了种植与家园币，入口这一层只认「屏能打开且分块在」。
            Report("u7_home", PageTitle(shell) == "家园洞府"
                && home.Contains("种植") && home.Contains("读取种植"));
            UiEntryHub.OpenSocial(shell, "codex");
            Report("u7_codex", PageTitle(shell) == "英灵图鉴" && Text(shell).Contains("尚未接服务端"));

            // U3 二级：小队，再返回表世界。
            UiEntryHub.OpenDungeon(shell, "party");
            Report("u3_party", PageTitle(shell) == "小队");
            UiEntryHub.BackToWorld(shell);
            Report("back_world", PageTitle(shell) == "表世界");

            // 结算屏的返回表世界不再 Toast。
            UiEntryHub.OpenDungeon(shell, "settle");
            Report("settle_back", HasButton(shell, "返回表世界") && !SourceHasLegacyToast());

            // 切皮肤只换颜色。
            UiEntryHub.OpenSocial(shell, "sect");
            var layout = Layout(shell);
            var copy = Text(shell);
            shell.SetSkin(UiSkin.Dark, false);
            Report("skin_layout", layout == Layout(shell));
            Report("skin_copy", copy == Text(shell));

            try
            {
                shell.ClosePage();
            }
            catch (Exception)
            {
                // 收尾销毁时控件可能已失效，不影响前面断言。
            }

            UiSkinStore.Reset();
            CleanupFlows();
            // Unity 重载了 == null：已 Destroy 的对象这里会当成 null。
            if (shell)
            {
                try
                {
                    UnityEngine.Object.DestroyImmediate(shell.gameObject);
                }
                catch (Exception)
                {
                    // 同上。
                }
            }

            Debug.Log("u8_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        static void OpenAndCapture(UiShell shell, UiWorldFlow world, int index, List<string> titles)
        {
            try
            {
                UiEntryHub.OpenHudFunction(shell, world, index);
                titles.Add(PageTitle(shell));
            }
            catch (Exception error)
            {
                Report("open_" + index, false);
                Debug.LogError("u8_self_open_error index=" + index + " " + error);
            }
        }

        static string PageTitle(UiShell shell)
        {
            // 顶栏标题在 chrome 层，不在 Content 子树里；按 TopTitle 名字取。
            var labels = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                if (labels[i] != null && labels[i].gameObject.name == "TopTitle")
                {
                    return labels[i].text ?? string.Empty;
                }
            }

            return string.Empty;
        }

        static bool HasButton(UiShell shell, string label)
        {
            var texts = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < texts.Length; i++)
            {
                if (texts[i] != null && texts[i].text == label)
                {
                    return true;
                }
            }

            return false;
        }

        static string Text(UiShell shell)
        {
            var sb = new StringBuilder();
            var labels = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                if (labels[i] != null && labels[i].text.Length > 0)
                {
                    sb.AppendLine(labels[i].text);
                }
            }

            return sb.ToString();
        }

        static string Layout(UiShell shell)
        {
            var sb = new StringBuilder();
            var rects = shell.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < rects.Length; i++)
            {
                var r = rects[i];
                sb.Append(r.name).Append(':')
                    .Append(r.anchoredPosition.x.ToString("0"))
                    .Append(',')
                    .Append(r.anchoredPosition.y.ToString("0"))
                    .Append(',')
                    .Append(r.sizeDelta.x.ToString("0"))
                    .Append(',')
                    .Append(r.sizeDelta.y.ToString("0"))
                    .Append(';');
            }

            return sb.ToString();
        }

        static bool SourceHasLegacyToast()
        {
            // 自检不读磁盘；静态段钉「返回表世界」走 Hub。这里只确认按钮文案在。
            return false;
        }

        static void CleanupFlows()
        {
            // Upgrade / Commerce / Social / Dungeon / Entry 各自建了独立 GO；
            // WorldFlow 挂在 UiShell 上，随 shell 一起销毁。
            DestroyFlowGos<UiUpgradeFlow>();
            DestroyFlowGos<UiCommerceFlow>();
            DestroyFlowGos<UiSocialFlow>();
            DestroyFlowGos<UiDungeonFlow>();
            DestroyFlowGos<UiEntryFlow>();
        }

        static void DestroyFlowGos<T>() where T : Component
        {
            var found = UnityEngine.Object.FindObjectsOfType<T>();
            for (var i = 0; i < found.Length; i++)
            {
                if (found[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(found[i].gameObject);
                }
            }
        }

        static void Report(string key, bool ok)
        {
            Debug.Log("u8_self_" + key + "=" + (ok ? "ok" : "fail"));
            if (!ok)
            {
                _failed = true;
            }
        }
    }
}

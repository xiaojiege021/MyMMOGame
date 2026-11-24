#if UNITY_EDITOR
using System;
using System.Text;
using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// N3 名册屏自检：逐条名单只跟 GET /sect/roster 的回包走；没有回包写「尚未请求」。
    /// </summary>
    public static class UiRosterSelfCheck
    {
        static bool _failed;

        public static bool Run()
        {
            _failed = false;
            UiSectScreen.ResetRosterWindow();
            UiSkinStore.Reset();
            var shell = UiShell.Ensure();
            shell.SetSkin(UiSkin.Light, false);
            var flow = UiSocialFlow.Ensure(shell);
            flow.Bind("n3-self-token", 9);

            flow.SetSender((path, body, token, done) =>
            {
                if (path.IndexOf("/sect/roster?sect_id=", StringComparison.Ordinal) >= 0)
                {
                    done(200, _reply);
                    return;
                }

                done(404, "{}");
            });

            shell.ShowPage(UiStrings.UiRosterSelfCheck.T001, s => UiSectScreen.Build(s, flow));
            var pending = Text(shell);
            Report("pending", pending.Contains(UiStrings.UiRosterSelfCheck.T002) && pending.Contains(UiStrings.UiRosterSelfCheck.T003)
                && !pending.Contains("11 · master") && !pending.Contains(UiStrings.UiRosterSelfCheck.T004));
            Debug.Log("n3_self_pending=" + (pending.Contains(UiStrings.UiRosterSelfCheck.T002) ? UiStrings.UiRosterSelfCheck.T002 : "missing"));

            _reply = TwoJson;
            flow.LoadRoster(7);
            shell.ShowPage(UiStrings.UiRosterSelfCheck.T001, s => UiSectScreen.Build(s, flow));
            var first = Text(shell);
            Report("rows", flow.HasRoster && flow.LastRoster.Ok && flow.LastRoster.Members.Count == 2
                && first.Contains("11 · master") && first.Contains("12 · disciple")
                && first.Contains(UiStrings.UiRosterSelfCheck.T005));
            Debug.Log("n3_self_rows=" + (first.Contains("11 · master") ? "11 · master" : "missing"));

            _reply = OneJson;
            flow.LoadRoster(7);
            shell.ShowPage(UiStrings.UiRosterSelfCheck.T001, s => UiSectScreen.Build(s, flow));
            var second = Text(shell);
            Report("rows_second", flow.LastRoster.Members.Count == 1
                && second.Contains("21 · elder")
                && !second.Contains("11 · master")
                && !second.Contains("12 · disciple"));
            Debug.Log("n3_self_rows_second=" + (second.Contains("21 · elder") ? "21 · elder" : "missing"));

            _reply = EmptyJson;
            flow.LoadRoster(7);
            shell.ShowPage(UiStrings.UiRosterSelfCheck.T001, s => UiSectScreen.Build(s, flow));
            var empty = Text(shell);
            Report("empty", flow.LastRoster.Ok && flow.LastRoster.Members.Count == 0
                && empty.Contains(UiStrings.UiRosterSelfCheck.T004)
                && !empty.Contains("21 · elder")
                && !empty.Contains("11 · master"));

            _reply = RejectJson;
            flow.LoadRoster(7);
            shell.ShowPage(UiStrings.UiRosterSelfCheck.T001, s => UiSectScreen.Build(s, flow));
            var rejected = Text(shell);
            Report("reject", !flow.LastRoster.Ok
                && rejected.Contains(UiStrings.UiRosterSelfCheck.T006)
                && !rejected.Contains("31 ·")
                && !rejected.Contains("11 · master"));

            _reply = CapJson;
            flow.LoadRoster(7);
            shell.ShowPage(UiStrings.UiRosterSelfCheck.T001, s => UiSectScreen.Build(s, flow));
            var capped = Text(shell);
            // 左栏规则句里本来就有 member_cap 这个词。这里只核对名单没把回包里
            // 多出来的上限数字画上去。
            Report("no_cap", capped.Contains("11 · master")
                && !capped.Contains("99")
                && !capped.Contains("11 · master ·"));

            _reply = PageJson;
            flow.LoadRoster(7);
            shell.ShowPage(UiStrings.UiRosterSelfCheck.T001, s => UiSectScreen.Build(s, flow));
            var page0 = Text(shell);
            UiSectScreen.StepRosterWindow(1, 3);
            shell.ShowPage(UiStrings.UiRosterSelfCheck.T001, s => UiSectScreen.Build(s, flow));
            var page1 = Text(shell);
            Report("page", page0.Contains("31 · disciple") && page0.Contains("32 · disciple")
                && !page0.Contains("33 · disciple")
                && page1.Contains("33 · disciple")
                && !page1.Contains("31 · disciple")
                && page1.Contains(UiStrings.UiRosterSelfCheck.T007));
            Debug.Log("n3_self_page=" + (page1.Contains("33 · disciple") ? "33" : "missing"));

            shell.ShowPage(UiStrings.UiRosterSelfCheck.T001, s => UiSectScreen.Build(s, flow));
            var layout = Layout(shell);
            var copy = Text(shell);
            shell.SetSkin(UiSkin.Dark, false);
            Report("skin_layout", layout == Layout(shell));
            Report("skin_copy", copy == Text(shell));

            shell.ClosePage();
            UiSkinStore.Reset();
            UiSectScreen.ResetRosterWindow();
            if (flow)
            {
                UnityEngine.Object.DestroyImmediate(flow.gameObject);
            }

            if (shell)
            {
                try { UnityEngine.Object.DestroyImmediate(shell.gameObject); }
                catch (Exception) { }
            }

            Debug.Log("n3_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        static string _reply = string.Empty;

        const string TwoJson =
            "{\"ok\":true,\"reason\":\"\",\"sect_id\":7,\"members\":["
            + "{\"character_id\":11,\"position\":\"master\"},"
            + "{\"character_id\":12,\"position\":\"disciple\"}]}";

        const string OneJson =
            "{\"ok\":true,\"reason\":\"\",\"sect_id\":7,\"members\":["
            + "{\"character_id\":21,\"position\":\"elder\"}]}";

        const string EmptyJson =
            "{\"ok\":true,\"reason\":\"\",\"sect_id\":7,\"members\":[]}";

        const string RejectJson =
            "{\"ok\":false,\"reason\":\"not_in_sect\",\"sect_id\":7,\"members\":["
            + "{\"character_id\":31,\"position\":\"master\"}]}";

        const string CapJson =
            "{\"ok\":true,\"reason\":\"\",\"sect_id\":7,\"member_cap\":99,\"seat_cap\":8,\"members\":["
            + "{\"character_id\":11,\"position\":\"master\",\"member_cap\":99}]}";

        const string PageJson =
            "{\"ok\":true,\"reason\":\"\",\"sect_id\":7,\"members\":["
            + "{\"character_id\":31,\"position\":\"disciple\"},"
            + "{\"character_id\":32,\"position\":\"disciple\"},"
            + "{\"character_id\":33,\"position\":\"disciple\"}]}";

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
                    .Append(r.anchoredPosition.x.ToString("0")).Append(',')
                    .Append(r.anchoredPosition.y.ToString("0")).Append(',')
                    .Append(r.sizeDelta.x.ToString("0")).Append(',')
                    .Append(r.sizeDelta.y.ToString("0")).Append(';');
            }

            return sb.ToString();
        }

        static void Report(string key, bool ok)
        {
            Debug.Log("n3_self_" + key + "=" + (ok ? "ok" : "fail"));
            if (!ok)
            {
                _failed = true;
            }
        }
    }
}
#endif

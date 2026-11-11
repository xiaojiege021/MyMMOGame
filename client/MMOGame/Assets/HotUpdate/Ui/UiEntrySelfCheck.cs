#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U2 四屏自检。`HotUpdate` 是 `autoReferenced: false`，`Assets/Editor` 引不到这里，
    /// 所以实例断言放在本程序集，由编辑器入口反射调 <see cref="Run"/>。
    ///
    /// 验四件事，正对第 14 节第 72 行的四条完成标准：
    /// 1. 口令不对 / 连不上 / 503 三句话在屏上分得开，且都不往下走；
    /// 2. 界面不本地判登录——没有令牌就出不去登入屏；
    /// 3. 立身屏只有四种出身与五个职业，分支一个都不列；
    /// 4. 载入进度走到头也不算能进游戏，只有服务端确认才放行。
    ///
    /// 回包靠替换发送实现喂进去，不连网关：这一层验的是「回包 → 屏上说什么」，
    /// 真网关那一段由 `tools/check/check-u2.sh` 用同一个 `AccountEntry` 验。
    /// </summary>
    public static class UiEntrySelfCheck
    {
        static bool _failed;

        static readonly List<string> Sent = new List<string>();

        static string _lastBody = string.Empty;

        static string _lastToken = string.Empty;

        static int _nextStatus;

        static string _nextBody = string.Empty;

        /// <summary>跑一遍。返回是否全过。</summary>
        public static bool Run()
        {
            _failed = false;

            UiSkinStore.Reset();
            var shell = UiShell.Ensure();
            var flow = UiEntryFlow.Ensure(shell);
            flow.Reset();
            flow.SetSender((path, body, token, done) =>
            {
                Sent.Add(path);
                _lastBody = body;
                _lastToken = token ?? string.Empty;
                done(_nextStatus, _nextBody);
            });

            try
            {
                CheckLogin(flow);
                CheckCreate(flow);
                CheckRosterAndLoading(flow, shell);
                CheckSkin(shell);
                CheckVerseBag();
            }
            catch (Exception error)
            {
                Report("exception", false);
                Debug.LogError("u2_self_stack " + error);
                _failed = true;
            }

            flow.Reset();
            UnityEngine.Object.DestroyImmediate(flow.gameObject);
            if (shell != null)
            {
                shell.ClosePage();
                UnityEngine.Object.DestroyImmediate(shell.gameObject);
            }

            UiSkinStore.Reset();
            Debug.Log("u2_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        // ------------------------------------------------------------------ 登入

        static void CheckLogin(UiEntryFlow flow)
        {
            flow.OpenLogin();
            Report("login_page", flow.Current == UiEntryFlow.Step.Login);

            // 空账号空口令：按钮置灰、一条请求都不发。
            var before = Sent.Count;
            flow.SubmitLogin();
            Report("login_empty_no_request", Sent.Count == before);

            // 401：屏上一句「账号或口令不对」，不进下一屏、不留令牌。
            Reply(401, "{\"error\":\"invalid_credentials\"}");
            flow.FillLogin("p11a1_hero", "wrong-pass");
            flow.SubmitLogin();
            var bad = LastHint(flow);
            Report("login_401", flow.Current == UiEntryFlow.Step.Login
                && flow.Token.Length == 0 && bad == UiEntryFlow.HintOf(EntryKind.InvalidCredentials));

            // 连不上网关：状态 0，说法必须和 401 不一样。
            Reply(0, string.Empty);
            flow.FillLogin("p11a1_hero", "wrong-pass");
            flow.SubmitLogin();
            var net = LastHint(flow);
            Report("login_network", flow.Current == UiEntryFlow.Step.Login && net == UiEntryFlow.HintOf(EntryKind.Network));

            // 503：第三句，仍然不进下一屏。
            Reply(503, "{\"error\":\"account_store_unavailable\"}");
            flow.FillLogin("p11a1_hero", "wrong-pass");
            flow.SubmitLogin();
            var unavailable = LastHint(flow);
            Report("login_503", flow.Current == UiEntryFlow.Step.Login
                && unavailable == UiEntryFlow.HintOf(EntryKind.Unavailable));

            // 三句话互不相同、都不为空——「分得开」就是这一条。
            var distinct = bad.Length > 0 && net.Length > 0 && unavailable.Length > 0
                && bad != net && bad != unavailable && net != unavailable;
            Report("login_distinct", distinct);
            Debug.Log("u2_self_hints=" + bad + " | " + net + " | " + unavailable);

            // 400 也不能被当成登录成功。
            Reply(400, "{\"error\":\"bad_request\"}");
            flow.FillLogin("p11a1_hero", "wrong-pass");
            flow.SubmitLogin();
            Report("login_400", flow.Current == UiEntryFlow.Step.Login && flow.Token.Length == 0);

            // 200 才给令牌，且令牌是回包里那一个。
            Reply(200, "{\"token\":\"tok-u2-selfcheck\",\"expires_in_s\":7200}");
            flow.FillLogin("p11a1_hero", "p11a1-Pw-52d8");
            flow.SubmitLogin();
            Report("login_200", flow.Current == UiEntryFlow.Step.Create
                && flow.Token == "tok-u2-selfcheck" && flow.LastKind == EntryKind.Ok);

            // 界面没有本地判登录：口令错到 200 之前，令牌一直是空的。
            Report("login_no_local_verdict", !HasLocalPasswordRule());
        }

        /// <summary>最后一行提示。登入屏只有那一处详情。</summary>
        static string LastHint(UiEntryFlow flow)
        {
            var shell = UiShell.Ensure();
            var labels = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                if (labels[i].name == "Label" && labels[i].text.Length > 0
                    && labels[i].text != "登 入" && labels[i].text != "正在校验")
                {
                    // 提示是屏上唯一会随回包变化的短句，按文案表比对即可。
                    if (labels[i].text == UiEntryFlow.HintOf(EntryKind.InvalidCredentials)
                        || labels[i].text == UiEntryFlow.HintOf(EntryKind.Network)
                        || labels[i].text == UiEntryFlow.HintOf(EntryKind.Unavailable)
                        || labels[i].text == UiEntryFlow.HintOf(EntryKind.BadRequest)
                        || labels[i].text == UiEntryFlow.HintOf(EntryKind.InvalidToken)
                        || labels[i].text == UiEntryFlow.HintOf(EntryKind.NameTaken)
                        || labels[i].text == UiEntryFlow.HintOf(EntryKind.Rejected))
                    {
                        return labels[i].text;
                    }
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 界面不本地判登录：没有一点「拿口令去比对」的写法，令牌只有一处赋值、
        /// 来源就是回包，没有写死的令牌。
        /// </summary>
        static bool HasLocalPasswordRule()
        {
            var files = new[] { "UiEntryFlow", "UiLoginScreen" };
            var compares = false;
            for (var i = 0; i < files.Length; i++)
            {
                var text = ReadClientFile("HotUpdate/Ui/" + files[i] + ".cs");
                if (text.Contains("password ==") || text.Contains("Password ==")
                    || text.Contains("password.Equals") || text.Contains("Password.Equals")
                    || text.Contains("password.Length =="))
                {
                    compares = true;
                }
            }

            var flow = ReadClientFile("HotUpdate/Ui/UiEntryFlow.cs");
            var fromReply = Count(flow, "Token = outcome.Token;") == 1;
            var hardcoded = flow.Contains("Token = \"");
            return !compares && fromReply && !hardcoded;
        }

        /// <summary>数一段文本里出现了几次。核对只认短片段，够用。</summary>
        static int Count(string text, string needle)
        {
            var total = 0;
            var at = 0;
            while (true)
            {
                var hit = text.IndexOf(needle, at, StringComparison.Ordinal);
                if (hit < 0)
                {
                    return total;
                }

                total++;
                at = hit + needle.Length;
            }
        }

        // ------------------------------------------------------------------ 立身

        static void CheckCreate(UiEntryFlow flow)
        {
            flow.OpenCreate();
            var shell = UiShell.Ensure();

            // 四种出身 + 五个职业 = 九个可选项，一个分支都没有。
            var options = new List<string>();
            var rects = shell.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < rects.Length; i++)
            {
                if (rects[i].name != "Option")
                {
                    continue;
                }

                var texts = rects[i].GetComponentsInChildren<Text>(true);
                if (texts.Length > 0)
                {
                    options.Add(texts[0].text);
                }
            }

            Report("create_options", options.Count == UiEntryCodex.Races.Length + UiEntryCodex.Roles.Length);
            Debug.Log("u2_self_options=" + string.Join(",", options.ToArray()));

            var racesNamed = 0;
            for (var i = 0; i < UiEntryCodex.Races.Length; i++)
            {
                if (options.Contains(UiEntryCodex.Races[i].Name))
                {
                    racesNamed++;
                }
            }

            var rolesNamed = 0;
            for (var i = 0; i < UiEntryCodex.Roles.Length; i++)
            {
                if (options.Contains(UiEntryCodex.Roles[i].Name))
                {
                    rolesNamed++;
                }
            }

            Report("create_four_races", racesNamed == 4);
            Report("create_five_roles", rolesNamed == 5);

            // 三样不齐时按不下去，也不会发请求。
            var before = Sent.Count;
            flow.SubmitCreate(string.Empty, string.Empty, string.Empty);
            flow.SubmitCreate("chen", "warrior", string.Empty);
            Report("create_incomplete_no_request", Sent.Count == before);

            // 齐了才发，且请求里分支为空、不带等级与境界。
            Reply(200, "{\"character_id\":12,\"name\":\"顾长息\",\"race\":\"chen\","
                       + "\"class\":\"warrior\",\"level\":1,\"realm\":\"\"}");
            flow.FillName("顾长息");
            flow.SubmitCreate("chen", "warrior", "顾长息");
            var branchEmpty = _lastBody.Contains("\"branch\":\"\"");
            var noLevel = !_lastBody.Contains("level") && !_lastBody.Contains("realm");
            var appearanceOk = _lastBody.Contains("\\\"body\\\":\\\"common\\\"");
            Report("create_body", branchEmpty && noLevel && appearanceOk);
            Report("create_ok", flow.Current == UiEntryFlow.Step.Roster
                && flow.Character.Ok && flow.Character.CharacterId == 12);
            Debug.Log("u2_self_body=" + _lastBody);
        }

        // ------------------------------------------------------------------ 角色栏与载入

        static void CheckRosterAndLoading(UiEntryFlow flow, UiShell shell)
        {
            flow.OpenRoster();
            Report("roster_page", flow.Current == UiEntryFlow.Step.Roster);

            var texts = new List<string>();
            var labels = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                if (labels[i].text.Length > 0)
                {
                    texts.Add(labels[i].text);
                }
            }

            Report("roster_rows", texts.Contains("在世") && texts.Contains("未开启"));

            flow.EnterChosen();
            Report("loading_page", flow.Current == UiEntryFlow.Step.Loading && !flow.Entered);

            // 进度条自己走：多推几秒也只到 92%，不算能进游戏。
            for (var i = 0; i < 40; i++)
            {
                flow.Tick(1f);
            }

            var capped = flow.Progress <= 0.92f && !flow.Entered;
            Report("loading_progress_only", capped);
            Debug.Log("u2_self_progress=" + flow.Progress.ToString("0.00"));

            // 服务端拒绝：还在载入屏，进度不回退也不能当成功。
            flow.ConfirmServerEntry(false, "world_busy");
            Report("loading_refused", flow.Current == UiEntryFlow.Step.Loading && !flow.Entered);

            // 服务端确认：这才算进去了。
            flow.ConfirmServerEntry(true, string.Empty);
            Report("loading_entered", flow.Entered && flow.Progress >= 0.999f);
        }

        // ------------------------------------------------------------------ 皮肤

        static void CheckSkin(UiShell shell)
        {
            var flow = UiEntryFlow.Ensure(shell);
            flow.OpenLogin();

            shell.SetSkin(UiSkin.Light, false);
            var lightLayout = Layout(shell);
            var lightCopy = Copy(shell);
            var lightColor = Colors(shell);

            shell.SetSkin(UiSkin.Dark, false);
            Report("skin_layout", lightLayout == Layout(shell));
            Report("skin_copy", lightCopy == Copy(shell));
            Report("skin_color", lightColor != Colors(shell));

            shell.SetSkin(UiSkin.Light, false);
        }

        // ------------------------------------------------------------------ 题记

        static void CheckVerseBag()
        {
            UiEntryCodex.ResetVerseBag(20261111);
            var seen = new HashSet<string>();
            for (var i = 0; i < UiEntryCodex.Verses.Length; i++)
            {
                var verse = UiEntryCodex.NextVerse();
                seen.Add(verse[0]);
            }

            Report("verse_bag_unique", seen.Count == UiEntryCodex.Verses.Length);
            Report("verse_bag_refill", UiEntryCodex.BagLeft == 0);
            UiEntryCodex.NextVerse();
            Report("verse_bag_next_cycle", UiEntryCodex.BagLeft == UiEntryCodex.Verses.Length - 1);
        }

        // ------------------------------------------------------------------ 工具

        static void Reply(int status, string body)
        {
            _nextStatus = status;
            _nextBody = body;
        }

        static void Report(string key, bool ok)
        {
            Debug.Log("u2_self_" + key + "=" + (ok ? "ok" : "fail"));
            if (!ok)
            {
                _failed = true;
            }
        }

        static string Layout(UiShell shell)
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

        static string Copy(UiShell shell)
        {
            var builder = new StringBuilder();
            var labels = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                builder.Append(labels[i].name).Append(':').Append(labels[i].text).Append(';');
            }

            return builder.ToString();
        }

        static string Colors(UiShell shell)
        {
            var builder = new StringBuilder();
            var images = shell.GetComponentsInChildren<Image>(true);
            for (var i = 0; i < images.Length; i++)
            {
                builder.Append(images[i].color.r.ToString("0.###")).Append(',')
                    .Append(images[i].color.g.ToString("0.###")).Append(',')
                    .Append(images[i].color.b.ToString("0.###")).Append(';');
            }

            var labels = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                builder.Append(labels[i].color.r.ToString("0.###")).Append(',')
                    .Append(labels[i].color.g.ToString("0.###")).Append(',')
                    .Append(labels[i].color.b.ToString("0.###")).Append(';');
            }

            return builder.ToString();
        }

        static string ReadClientFile(string relative)
        {
            // Application.dataPath 指到 Assets，退三层到仓库根。
            var root = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "../../.."));
            var path = System.IO.Path.Combine(root, "client/MMOGame/Assets", relative);
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : string.Empty;
        }
    }
}
#endif

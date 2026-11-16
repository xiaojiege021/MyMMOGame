using System;
using System.Text;
using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U12 竞技场与战场进场自检：两条口都接 N5 的 `POST /versus/*`，屏上照抄
    /// 进场路径、模式人数、场次与队伍、职业分支；没回包写「尚未请求」，ok=false 照抄原因，
    /// 非 200 写回执，本机不算战力、不乘人数、不做匹配。
    /// </summary>
    public static class UiArenaSelfCheck
    {
        static bool _failed;

        static int _status = 200;

        static string _arenaJson = ArenaDuelJson;

        static string _battlefieldJson = BattlefieldJson;

        static string _lastPath = string.Empty;

        static string _lastBody = string.Empty;

        public static bool Run()
        {
            _failed = false;
            _status = 200;
            _arenaJson = ArenaDuelJson;
            _battlefieldJson = BattlefieldJson;
            _lastPath = string.Empty;
            _lastBody = string.Empty;

            UiSkinStore.Reset();
            var shell = UiShell.Ensure();
            shell.SetSkin(UiSkin.Light, false);
            var flow = UiSocialFlow.Ensure(shell);
            flow.Bind("u12-self-token", 9);

            flow.SetSender((path, body, token, done) =>
            {
                _lastPath = path;
                _lastBody = body;

                if (path.IndexOf(VersusEntry.ArenaPath, StringComparison.Ordinal) >= 0)
                {
                    done(_status, _status == 200 ? _arenaJson : "{\"error\":\"character_not_found\"}");
                    return;
                }

                if (path.IndexOf(VersusEntry.BattlefieldPath, StringComparison.Ordinal) >= 0)
                {
                    done(_status, _status == 200 ? _battlefieldJson : "{\"error\":\"character_not_found\"}");
                    return;
                }

                done(404, "{}");
            });

            // 一、尚未请求：不画路径、不画人数，也不给假数字。
            shell.ShowPage("竞技场与战场", s => UiArenaScreen.Build(s, flow));
            var pending = Text(shell);
            Report("pending", pending.Contains("尚未请求")
                && pending.Contains("进竞技场")
                && pending.Contains("A 队") && pending.Contains("B 队")
                && !pending.Contains("进场路径")
                && !pending.Contains("已接 /versus/*"));
            Debug.Log("u12_self_pending=" + pending.Contains("进竞技场"));

            // 二、竞技场 1v1：进场路径与人数照抄回包，请求体只带角色号与模式编号。
            flow.EnterArena(UiSocialCodex.ArenaModeIds[0]);
            shell.ShowPage("竞技场与战场", s => UiArenaScreen.Build(s, flow));
            var arena = Text(shell);
            Report("arena", flow.HasArena && flow.LastArena.Ok
                && flow.LastArena.ArenaMode == "duel"
                && flow.LastArena.PartySize == 2
                && flow.LastArena.BattlePath == "/battle"
                && arena.Contains("进场路径 /battle · 模式 duel · 人数 2")
                && arena.Contains("已接 /versus/*")
                && arena.Contains("竞技场 · 属性模板统一"));
            Report("body_only", _lastPath == VersusEntry.ArenaPath
                && _lastBody.Contains("\"character_id\":9")
                && _lastBody.Contains("\"arena_mode\":\"duel\"")
                && _lastBody.IndexOf("class_id", StringComparison.Ordinal) < 0
                && _lastBody.IndexOf("party_size", StringComparison.Ordinal) < 0);
            Debug.Log("u12_self_arena mode=" + flow.LastArena.ArenaMode
                + " party_size=" + flow.LastArena.PartySize + " body=" + _lastBody);

            // 三、3v3：人数只跟回包走（回包说 6 就 6），屏上不按 1v1 的 2 乘三。
            _arenaJson = ArenaTrioJson;
            flow.EnterArena(UiSocialCodex.ArenaModeIds[1]);
            shell.ShowPage("竞技场与战场", s => UiArenaScreen.Build(s, flow));
            var trio = Text(shell);
            Report("no_local_multiply", flow.LastArena.PartySize == 6
                && trio.Contains("进场路径 /battle · 模式 trio · 人数 6")
                && !trio.Contains("人数 2 ·"));
            Debug.Log("u12_self_trio party_size=" + flow.LastArena.PartySize);

            // 四、回包 ok=false：照抄原因，不编一个成功路径，也不把上一次的顶上去。
            _arenaJson = ArenaRejectJson;
            flow.EnterArena(UiSocialCodex.ArenaModeIds[0]);
            shell.ShowPage("竞技场与战场", s => UiArenaScreen.Build(s, flow));
            var rejected = Text(shell);
            Report("reason", flow.HasArena && !flow.LastArena.Ok
                && rejected.Contains("服务端：arena_mode_unknown")
                && !rejected.Contains("进场路径 /battle · 模式 duel · 人数 2"));
            Debug.Log("u12_self_reason=" + flow.LastArena.Reason);

            // 五、非 200：写回执，不假装「尚未请求」。
            _status = 500;
            flow.EnterArena(UiSocialCodex.ArenaModeIds[0]);
            shell.ShowPage("竞技场与战场", s => UiArenaScreen.Build(s, flow));
            var failed = Text(shell);
            Report("reject", !flow.HasArena && flow.ArenaTried
                && failed.Contains("竞技场进场没回应：500")
                && failed.Contains("没读到")
                && !failed.Contains("尚未请求 · 点「进竞技场」"));
            Debug.Log("u12_self_reject=" + failed.Contains("竞技场进场没回应：500"));

            // 六、战场：两条竞技场口径都失败过，这时再看战场这一条自己成不成。
            //     场次与队伍照抄回包，模式行指到战场那一层。
            _status = 200;
            flow.EnterBattlefield(UiSocialCodex.BattlefieldMatchIds[0], UiSocialCodex.BattlefieldTeams[0]);
            shell.ShowPage("竞技场与战场", s => UiArenaScreen.Build(s, flow));
            var field = Text(shell);
            Report("battlefield", flow.HasBattlefield && flow.LastBattlefield.Ok
                && flow.LastBattlefield.Battlefield == "bf_m1"
                && flow.LastBattlefield.Team == "a"
                && _lastPath == VersusEntry.BattlefieldPath
                && field.Contains("场次 bf_m1 · 队伍 a")
                && field.Contains("战场 · 装备生效"));
            Debug.Log("u12_self_battlefield match=" + flow.LastBattlefield.Battlefield
                + " team=" + flow.LastBattlefield.Team + " body=" + _lastBody);

            // 七、切皮肤只换颜色：布局与文案一字不差。
            _status = 200;
            _arenaJson = ArenaDuelJson;
            flow.EnterArena(UiSocialCodex.ArenaModeIds[0]);
            shell.ShowPage("竞技场与战场", s => UiArenaScreen.Build(s, flow));
            var layout = Layout(shell);
            var copy = Text(shell);
            shell.SetSkin(UiSkin.Dark, false);
            Report("skin_layout", layout == Layout(shell));
            Report("skin_copy", copy == Text(shell));

            shell.ClosePage();
            UiSkinStore.Reset();
            if (flow)
            {
                UnityEngine.Object.DestroyImmediate(flow.gameObject);
            }

            if (shell)
            {
                try { UnityEngine.Object.DestroyImmediate(shell.gameObject); }
                catch (Exception) { }
            }

            Debug.Log("u12_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        /// <summary>网关的竞技场 1v1 回包形状（字段名与 `VersusApi` 一致）。</summary>
        const string ArenaDuelJson =
            "{\"ok\":true,\"reason\":\"\",\"battle_path\":\"/battle\",\"character_id\":9," +
            "\"class_id\":\"warrior\",\"branch_id\":\"\",\"arena_mode\":\"duel\"," +
            "\"party_size\":2,\"party\":\"arena-9f1c\"}";

        /// <summary>3v3 回包：人数由服务端给 6，屏上不许自己拿 2 乘三。</summary>
        const string ArenaTrioJson =
            "{\"ok\":true,\"reason\":\"\",\"battle_path\":\"/battle\",\"character_id\":9," +
            "\"class_id\":\"mage\",\"branch_id\":\"chixiao\",\"arena_mode\":\"trio\"," +
            "\"party_size\":6,\"party\":\"arena-7a02\"}";

        /// <summary>模式不在表里时网关回的那一份：200 但 ok=false。</summary>
        const string ArenaRejectJson =
            "{\"ok\":false,\"reason\":\"arena_mode_unknown\"}";

        /// <summary>战场回包：场次与队伍都在，`battlefield` 与 `match` 同值。</summary>
        const string BattlefieldJson =
            "{\"ok\":true,\"reason\":\"\",\"battle_path\":\"/battle\",\"character_id\":9," +
            "\"class_id\":\"warrior\",\"branch_id\":\"\",\"match\":\"bf_m1\",\"team\":\"a\"," +
            "\"battlefield\":\"bf_m1\"}";

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
            Debug.Log("u12_self_" + key + "=" + (ok ? "ok" : "fail"));
            if (!ok) _failed = true;
        }
    }
}

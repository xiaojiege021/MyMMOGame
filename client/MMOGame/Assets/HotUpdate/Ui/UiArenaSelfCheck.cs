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

        static string _rankJson = RankJson;

        static string _boardJson = BoardJson;

        static string _claimJson = ClaimJson;

        static string _lastPath = string.Empty;

        static string _lastBody = string.Empty;

        /// <summary>领外观那一次的请求体。领成功后会再读一次段位，_lastBody 会被那次 GET 顶掉，
        /// 所以这一条单独记。</summary>
        static string _claimBody = string.Empty;

        public static bool Run()
        {
            _failed = false;
            _status = 200;
            _arenaJson = ArenaDuelJson;
            _battlefieldJson = BattlefieldJson;
            _rankJson = RankJson;
            _boardJson = BoardJson;
            _claimJson = ClaimJson;
            _lastPath = string.Empty;
            _lastBody = string.Empty;
            _claimBody = string.Empty;

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

                // U19：段位、排行榜、领外观三条口。顺序上先认领外观这条最长的，
                // 再认段位与排行（三条前缀互不为前缀，这里只是写死一个稳定次序）。
                if (path.IndexOf(ArenaRank.ClaimPath, StringComparison.Ordinal) >= 0)
                {
                    _claimBody = body;
                    done(_status, _status == 200 ? _claimJson : "{\"error\":\"tier_not_reached\"}");
                    return;
                }

                if (path.IndexOf(ArenaRank.RankPath, StringComparison.Ordinal) >= 0)
                {
                    done(_status, _status == 200 ? _rankJson : "{\"error\":\"character_not_found\"}");
                    return;
                }

                if (path.IndexOf(ArenaRank.BoardPath, StringComparison.Ordinal) >= 0)
                {
                    done(_status, _status == 200 ? _boardJson : "{\"error\":\"season_not_open\"}");
                    return;
                }

                done(404, "{}");
            });

            // 一、尚未请求：不画路径、不画人数，也不给假数字。
            shell.ShowPage(UiStrings.UiArenaSelfCheck.T001, s => UiArenaScreen.Build(s, flow));
            var pending = Text(shell);
            Report("pending", pending.Contains(UiStrings.UiArenaSelfCheck.T002)
                && pending.Contains(UiStrings.UiArenaSelfCheck.T003)
                && pending.Contains(UiStrings.UiArenaSelfCheck.T004) && pending.Contains(UiStrings.UiArenaSelfCheck.T005)
                && !pending.Contains(UiStrings.UiArenaSelfCheck.T006)
                && !pending.Contains(UiStrings.UiArenaSelfCheck.T007));
            Debug.Log("u12_self_pending=" + pending.Contains(UiStrings.UiArenaSelfCheck.T003));

            // 二、竞技场 1v1：进场路径与人数照抄回包，请求体只带角色号与模式编号。
            flow.EnterArena(UiSocialCodex.ArenaModeIds[0]);
            shell.ShowPage(UiStrings.UiArenaSelfCheck.T001, s => UiArenaScreen.Build(s, flow));
            var arena = Text(shell);
            Report("arena", flow.HasArena && flow.LastArena.Ok
                && flow.LastArena.ArenaMode == "duel"
                && flow.LastArena.PartySize == 2
                && flow.LastArena.BattlePath == "/battle"
                && arena.Contains(UiStrings.UiArenaSelfCheck.T008)
                && arena.Contains(UiStrings.UiArenaSelfCheck.T007)
                && arena.Contains(UiStrings.UiArenaSelfCheck.T009));
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
            shell.ShowPage(UiStrings.UiArenaSelfCheck.T001, s => UiArenaScreen.Build(s, flow));
            var trio = Text(shell);
            Report("no_local_multiply", flow.LastArena.PartySize == 6
                && trio.Contains(UiStrings.UiArenaSelfCheck.T010)
                && !trio.Contains(UiStrings.UiArenaSelfCheck.T011));
            Debug.Log("u12_self_trio party_size=" + flow.LastArena.PartySize);

            // 四、回包 ok=false：照抄原因，不编一个成功路径，也不把上一次的顶上去。
            _arenaJson = ArenaRejectJson;
            flow.EnterArena(UiSocialCodex.ArenaModeIds[0]);
            shell.ShowPage(UiStrings.UiArenaSelfCheck.T001, s => UiArenaScreen.Build(s, flow));
            var rejected = Text(shell);
            Report("reason", flow.HasArena && !flow.LastArena.Ok
                && rejected.Contains(UiStrings.UiArenaSelfCheck.T012)
                && !rejected.Contains(UiStrings.UiArenaSelfCheck.T008));
            Debug.Log("u12_self_reason=" + flow.LastArena.Reason);

            // 五、非 200：写回执，不假装「尚未请求」。
            _status = 500;
            flow.EnterArena(UiSocialCodex.ArenaModeIds[0]);
            shell.ShowPage(UiStrings.UiArenaSelfCheck.T001, s => UiArenaScreen.Build(s, flow));
            var failed = Text(shell);
            Report("reject", !flow.HasArena && flow.ArenaTried
                && failed.Contains(UiStrings.UiArenaSelfCheck.T013)
                && failed.Contains(UiStrings.UiArenaSelfCheck.T014)
                && !failed.Contains(UiStrings.UiArenaSelfCheck.T015));
            Debug.Log("u12_self_reject=" + failed.Contains(UiStrings.UiArenaSelfCheck.T013));

            // 六、战场：两条竞技场口径都失败过，这时再看战场这一条自己成不成。
            //     场次与队伍照抄回包，模式行指到战场那一层。
            _status = 200;
            flow.EnterBattlefield(UiSocialCodex.BattlefieldMatchIds[0], UiSocialCodex.BattlefieldTeams[0]);
            shell.ShowPage(UiStrings.UiArenaSelfCheck.T001, s => UiArenaScreen.Build(s, flow));
            var field = Text(shell);
            Report("battlefield", flow.HasBattlefield && flow.LastBattlefield.Ok
                && flow.LastBattlefield.Battlefield == "bf_m1"
                && flow.LastBattlefield.Team == "a"
                && _lastPath == VersusEntry.BattlefieldPath
                && field.Contains(UiStrings.UiArenaSelfCheck.T016)
                && field.Contains(UiStrings.UiArenaSelfCheck.T017));
            Debug.Log("u12_self_battlefield match=" + flow.LastBattlefield.Battlefield
                + " team=" + flow.LastBattlefield.Team + " body=" + _lastBody);

            // 七、切皮肤只换颜色：布局与文案一字不差。
            _status = 200;
            _arenaJson = ArenaDuelJson;
            flow.EnterArena(UiSocialCodex.ArenaModeIds[0]);
            shell.ShowPage(UiStrings.UiArenaSelfCheck.T001, s => UiArenaScreen.Build(s, flow));
            var layout = Layout(shell);
            var copy = Text(shell);
            shell.SetSkin(UiSkin.Dark, false);
            Report("skin_layout", layout == Layout(shell));
            Report("skin_copy", copy == Text(shell));

            // ------------------------------------------------ 八、U19 段位屏（B11 欠项）
            //     分数、当前档、下一档门槛、每档领没领全照抄回包；门槛不在客户端比。
            _status = 200;
            flow.LoadArenaRank();
            shell.ShowPage(UiStrings.UiArenaSelfCheck.T018, s => UiArenaRankScreen.Build(s, flow));
            var rank = Text(shell);
            Report("rank_ok", flow.HasRank && flow.LastRank.Ok
                && flow.LastRank.Score == 260
                && flow.LastRank.TierKey == "silver"
                && flow.LastRank.NextTierKey == "gold"
                && flow.LastRank.Tiers.Count == 3
                && rank.Contains("260")
                && rank.Contains("Silver")
                && rank.Contains(UiStrings.UiArenaRankScreen.T007));

            // 一档一格：已领的写「已领」、够格的给「领取」按钮、没够的写「未达」；
            // 档位顺序就是回包顺序（Bronze → Silver → Gold），不按分数重排。
            // 只取「档位与专属外观」那块之后的行，避免当前档那一行（Silver）参与比序。
            var ladderAt = rank.IndexOf(UiStrings.UiArenaRankScreen.T010, StringComparison.Ordinal);
            var ladderText = ladderAt >= 0 ? rank.Substring(ladderAt) : rank;
            Report("rank_tiers", rank.Contains(UiStrings.UiArenaRankScreen.T015)
                && rank.Contains(UiStrings.UiArenaRankScreen.T014)
                && rank.Contains(UiStrings.UiArenaRankScreen.T016)
                && ladderText.IndexOf("Bronze", StringComparison.Ordinal) < ladderText.IndexOf("Silver", StringComparison.Ordinal)
                && ladderText.IndexOf("Silver", StringComparison.Ordinal) < ladderText.IndexOf("Gold", StringComparison.Ordinal));
            Debug.Log("u19_rank_screen score=" + flow.LastRank.Score + " tiers=" + flow.LastRank.Tiers.Count);

            // 九、领外观：请求体只有角色号与档位键；领成功后档位表跟着回包重画，不自己标已领。
            _claimJson = ClaimJson;
            _rankJson = RankClaimedJson;
            flow.ClaimAppearance("gold");
            shell.ShowPage(UiStrings.UiArenaSelfCheck.T018, s => UiArenaRankScreen.Build(s, flow));
            var claimed = Text(shell);
            Report("claim_ok", flow.HasClaim && flow.LastClaim.Ok
                && flow.LastClaim.ItemId == "outfit_arena_r2"
                && flow.LastClaim.BagSlot == 12
                && claimed.Contains("outfit_arena_r2")
                && flow.HasRank && flow.LastRank.Tiers[2].Claimed);
            Report("claim_body", _claimBody == "{\"character_id\":9,\"tier_key\":\"gold\"}");
            Debug.Log("u19_claim_body=" + _claimBody);

            // 十、领不了：照抄服务端原因，不把这一档标成已领。
            _rankJson = RankJson;
            _claimJson = ClaimRejectJson;
            flow.ClaimAppearance("gold");
            shell.ShowPage(UiStrings.UiArenaSelfCheck.T018, s => UiArenaRankScreen.Build(s, flow));
            Report("claim_reject", flow.HasClaim && !flow.LastClaim.Ok
                && Text(shell).Contains("tier_not_reached"));

            // 十一、排行榜：名次与行顺序照抄回包。回包里第二行分数更高（320 > 180），
            //       屏上仍按名次排——客户端不许按分数重排。
            _status = 200;
            _boardJson = BoardJson;
            flow.LoadLeaderboard();
            shell.ShowPage(UiStrings.UiArenaSelfCheck.T019, s => UiArenaBoardScreen.Build(s, flow));
            var board = Text(shell);
            Report("board_ok", flow.HasBoard && flow.LastBoard.Ok
                && flow.LastBoard.Rows.Count == 2
                && flow.LastBoard.Rows[0].Rank == 1
                && board.Contains("top_one") && board.Contains("320"));
            Report("board_order", flow.LastBoard.Rows[1].Score == 320
                && board.IndexOf("top_one", StringComparison.Ordinal) < board.IndexOf("second_one", StringComparison.Ordinal));
            Debug.Log("u19_board_screen rows=" + flow.LastBoard.Rows.Count);

            // 十二、排行榜被拒 / 非 200：照抄原因与回执，不假装「尚未请求」。
            _boardJson = BoardRejectJson;
            flow.LoadLeaderboard();
            shell.ShowPage(UiStrings.UiArenaSelfCheck.T019, s => UiArenaBoardScreen.Build(s, flow));
            Report("board_reject", !flow.HasBoard && Text(shell).Contains("season_not_open"));

            _status = 500;
            flow.LoadLeaderboard();
            shell.ShowPage(UiStrings.UiArenaSelfCheck.T019, s => UiArenaBoardScreen.Build(s, flow));
            Report("board_status", !flow.HasBoard && flow.BoardTried
                && Text(shell).Contains(UiStrings.ArenaRankFlow.T002 + "500"));

            // 十三、段位屏切皮肤只换颜色：布局与文案一字不差。
            _status = 200;
            _rankJson = RankJson;
            flow.LoadArenaRank();
            shell.ShowPage(UiStrings.UiArenaSelfCheck.T018, s => UiArenaRankScreen.Build(s, flow));
            shell.SetSkin(UiSkin.Dark, false);
            var rankLayout = Layout(shell);
            var rankCopy = Text(shell);
            shell.SetSkin(UiSkin.Light, false);
            Report("rank_skin_layout", rankLayout == Layout(shell));
            Report("rank_skin_copy", rankCopy == Text(shell));

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

        /// <summary>
        /// 段位回包（字段名与 `ArenaRankApi` 一致）：三档、当前白银、下一档黄金门槛 300。
        /// 青铜已领、白银够格没领、黄金没够——三种状态齐了才验得出「照抄两个布尔」。
        /// 名字用 ASCII，免得断言里再进一份中文。
        /// </summary>
        const string RankJson =
            "{\"ok\":true,\"reason\":\"\",\"season_id\":7,\"score\":260,\"tier_key\":\"silver\"," +
            "\"tier_name\":\"Silver\",\"wins\":12,\"next_tier_key\":\"gold\",\"next_tier_min_score\":300," +
            "\"tiers\":[" +
            "{\"key\":\"bronze\",\"name\":\"Bronze\",\"min_score\":0,\"appearance_item_id\":\"outfit_arena_r1\"," +
            "\"reached\":true,\"claimed\":true}," +
            "{\"key\":\"silver\",\"name\":\"Silver\",\"min_score\":150,\"appearance_item_id\":\"outfit_arena_r2\"," +
            "\"reached\":true,\"claimed\":false}," +
            "{\"key\":\"gold\",\"name\":\"Gold\",\"min_score\":300,\"appearance_item_id\":\"outfit_arena_r3\"," +
            "\"reached\":false,\"claimed\":false}]}";

        /// <summary>领完黄金那一档之后再读一次的回包：黄金变成够格且已领。</summary>
        const string RankClaimedJson =
            "{\"ok\":true,\"reason\":\"\",\"season_id\":7,\"score\":260,\"tier_key\":\"silver\"," +
            "\"tier_name\":\"Silver\",\"wins\":12,\"next_tier_key\":\"gold\",\"next_tier_min_score\":300," +
            "\"tiers\":[" +
            "{\"key\":\"bronze\",\"name\":\"Bronze\",\"min_score\":0,\"appearance_item_id\":\"outfit_arena_r1\"," +
            "\"reached\":true,\"claimed\":true}," +
            "{\"key\":\"silver\",\"name\":\"Silver\",\"min_score\":150,\"appearance_item_id\":\"outfit_arena_r2\"," +
            "\"reached\":true,\"claimed\":false}," +
            "{\"key\":\"gold\",\"name\":\"Gold\",\"min_score\":300,\"appearance_item_id\":\"outfit_arena_r3\"," +
            "\"reached\":true,\"claimed\":true}]}";

        /// <summary>领外观成功：绑进背包第 12 格。</summary>
        const string ClaimJson =
            "{\"ok\":true,\"reason\":\"\",\"season_id\":7,\"tier_key\":\"gold\"," +
            "\"item_id\":\"outfit_arena_r2\",\"bag_slot\":12,\"bound\":true,\"duplicate\":false}";

        /// <summary>领外观被拒：门槛不够时角色服回的那一份（200 但 ok=false）。</summary>
        const string ClaimRejectJson =
            "{\"ok\":false,\"reason\":\"tier_not_reached\",\"season_id\":7,\"tier_key\":\"gold\"," +
            "\"item_id\":\"\",\"bag_slot\":0,\"bound\":false,\"duplicate\":false}";

        /// <summary>
        /// 排行榜回包：第二行分数比第一行高（320 > 180），名次仍由回包给。
        /// 屏上要是自己按分数重排，这一条就会红。
        /// </summary>
        const string BoardJson =
            "{\"ok\":true,\"reason\":\"\",\"season_id\":7,\"limit\":2,\"entries\":[" +
            "{\"rank\":1,\"character_id\":31,\"name\":\"top_one\",\"class_id\":1,\"score\":180," +
            "\"tier_key\":\"silver\",\"tier_name\":\"Silver\"}," +
            "{\"rank\":2,\"character_id\":32,\"name\":\"second_one\",\"class_id\":2,\"score\":320," +
            "\"tier_key\":\"gold\",\"tier_name\":\"Gold\"}]}";

        /// <summary>季没开时排行榜回的那一份。</summary>
        const string BoardRejectJson =
            "{\"ok\":false,\"reason\":\"season_not_open\",\"season_id\":0,\"limit\":0,\"entries\":[]}";

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

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U3 四屏自检。`HotUpdate` 是 `autoReferenced: false`，`Assets/Editor` 引不到这里，
    /// 所以实例断言放在本程序集，由编辑器入口反射调 <see cref="Run"/>。
    ///
    /// 验的正对第 14 节第 73 行的三条完成标准与 14.1 的两条口径：
    /// 1. 屏上的疲劳 / 日上限 / 本周剩余 / 每人份数逐字等于回包，回包没到就写「尚未进本」；
    /// 2. 被拒时停在原屏，原因用服务端给的那一条，不自己判；
    /// 3. 结算的通关与否、事务号、经验只认 `Ended` 回包；
    /// 4. 小队名单只画战斗服下发的人，名单没到就说没到；
    /// 5. 切皮肤只换颜色，文案一字不差。
    ///
    /// 回包靠替换发送实现喂进去，不连网关：这一层验的是「回包 → 屏上说什么」，
    /// 真网关那一段由 `tools/check/check-u3.sh` 用同一个 `DungeonEntry` 验。
    /// </summary>
    public static class UiBattleSelfCheck
    {
        static bool _failed;

        static string _lastPath = string.Empty;

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
            var flow = UiDungeonFlow.Ensure(shell);
            flow.SetSender((path, body, token, done) =>
            {
                _lastPath = path;
                _lastBody = body;
                _lastToken = token ?? string.Empty;
                done(_nextStatus, _nextBody);
            });
            flow.Configure("token-u3-self", 42);

            var built = 0;
            var members = 0;

            try
            {
                shell.ShowPage("裂隙", s => { built = UiDungeonScreen.Build(s, flow); });
                CheckDungeonList(built);
                CheckDungeonInitial(flow);
                CheckDungeonBody(flow);
                CheckDungeonAllowed(flow);
                CheckDungeonRefused(flow);
                CheckDungeonErrors(flow);
                CheckPartySizes(flow);

                shell.ShowPage("战斗结算", s => UiSettleScreen.Build(s, flow));
                CheckSettle(flow);

                shell.ShowPage("小队", s => { members = UiPartyScreen.Build(s, flow); });
                CheckParty(members, flow);

                CheckSkinKeepsCopy(shell, flow);
            }
            catch (Exception error)
            {
                Report("exception", false);
                Debug.LogError("u3_self_stack " + error);
                _failed = true;
            }

            UnityEngine.Object.DestroyImmediate(flow);
            if (shell != null)
            {
                shell.ClosePage();
                UnityEngine.Object.DestroyImmediate(shell.gameObject);
            }

            UiSkinStore.Reset();
            Debug.Log("u3_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        // ------------------------------------------------------------------ C03

        static void CheckDungeonList(int built)
        {
            var codex = UiBattleCodex.Dungeons;
            var ok = built == 3 && codex.Length == 3
                && codex[0].Id == "main" && codex[0].MaxPlayers == 1
                && codex[1].Id == "abyss" && codex[1].MaxPlayers == 4
                && codex[2].Id == "lair" && codex[2].MaxPlayers == 4;
            Report("dungeon_list", ok);

            // 配置里没有的副本不许出现在可点的行里。
            UiBattleCodex.Dungeon ignored;
            var notListed = !UiBattleCodex.FindDungeon("dragon", out ignored)
                && !UiBattleCodex.FindDungeon("world_boss", out ignored)
                && !UiBattleCodex.FindDungeon("sect", out ignored);
            Report("dungeon_not_yet", notListed);
        }

        static void CheckDungeonInitial(UiDungeonFlow flow)
        {
            // 一次都没发过请求时，屏上不拿配置里的数假装已经确认。
            var text = flow.ScreenResultText;
            Report("dungeon_initial", !flow.HasReply && text.Contains("尚未进本"));
            Report("dungeon_enter_enabled", flow.EnterEnabled);
        }

        static void CheckDungeonBody(UiDungeonFlow flow)
        {
            flow.Choose("abyss");
            flow.StepParty(1);
            flow.SubmitEnter();

            var body = _lastBody;
            var ok = _lastPath == "/dungeon/enter"
                && body.Contains("\"character_id\":42")
                && body.Contains("\"dungeon_id\":\"abyss\"")
                && body.Contains("\"party_size\":2")
                && _lastToken == "token-u3-self"
                // 请求里不许带任何能左右结果的数值。
                && !body.Contains("fatigue")
                && !body.Contains("share")
                && !body.Contains("exp");
            Report("dungeon_body", ok);
        }

        static void CheckDungeonAllowed(UiDungeonFlow flow)
        {
            _nextStatus = 200;
            _nextBody = "{\"allowed\":true,\"reason\":\"\",\"max_players\":4,\"fatigue\":4500,"
                + "\"weekly_share\":0,\"entry_id\":\"77\",\"fatigue_cost\":3000,"
                + "\"fatigue_daily_cap\":6000,\"weekly_share_cost\":0,"
                + "\"weekly_share_remaining\":12,\"weekly_share_cap\":12,\"reward_shares\":2}";
            flow.SubmitEnter();

            var expected = "疲劳 4500 / 6000 · 本次扣 3000 · 本周剩 12 / 12 · 每人 2 份";
            Report("dungeon_allowed_kind", flow.Last.Kind == Mmo.Net.EnterKind.Allowed);
            Report("dungeon_allowed_copy", flow.ScreenResultText == expected);
            Report("dungeon_allowed_hint", flow.ScreenHintText.Contains("已进本"));
            Report("dungeon_allowed_cost", flow.Last.FatigueCost == 3000 && flow.Last.RewardShares == 2);
        }

        static void CheckDungeonRefused(UiDungeonFlow flow)
        {
            // 疲劳不够：200 但 allowed=false。屏上要停在原处，原因用服务端给的那条。
            _nextStatus = 200;
            _nextBody = "{\"allowed\":false,\"reason\":\"fatigue_exhausted\",\"max_players\":4,"
                + "\"fatigue\":6000,\"fatigue_cost\":0,\"fatigue_daily_cap\":6000,"
                + "\"weekly_share_remaining\":12,\"weekly_share_cap\":12,\"reward_shares\":0}";
            flow.SubmitEnter();

            Report("dungeon_refused_kind", flow.Last.Kind == Mmo.Net.EnterKind.Refused);
            Report("dungeon_refused_hint", flow.ScreenHintText.Contains("疲劳不够"));
            // 被拒时屏上的疲劳仍是回包那个 6000，不拿本地值凑。
            Report("dungeon_refused_numbers", flow.ScreenResultText.Contains("6000 / 6000"));
        }

        static void CheckDungeonErrors(UiDungeonFlow flow)
        {
            _nextStatus = 401;
            _nextBody = "{\"error\":\"invalid_token\"}";
            flow.SubmitEnter();
            Report("dungeon_auth", flow.Last.Kind == Mmo.Net.EnterKind.InvalidToken
                && flow.ScreenHintText.Contains("令牌失效"));

            _nextStatus = 0;
            _nextBody = string.Empty;
            flow.SubmitEnter();
            Report("dungeon_network", flow.Last.Kind == Mmo.Net.EnterKind.Network
                && flow.ScreenHintText.Contains("连不上网关"));

            _nextStatus = 503;
            _nextBody = "{\"error\":\"db_unavailable\"}";
            flow.SubmitEnter();
            Report("dungeon_unavailable", flow.Last.Kind == Mmo.Net.EnterKind.Unavailable
                && flow.ScreenHintText.Contains("暂时连不上"));
        }

        static void CheckPartySizes(UiDungeonFlow flow)
        {
            // 人数只能在 1 到这张副本的 max_players 之间动，上下限来自配置表。
            flow.Choose("main");
            flow.StepParty(3);
            Report("party_clamp_max", flow.PartySize == 1);

            flow.Choose("abyss");
            flow.StepParty(9);
            var high = flow.PartySize;
            flow.StepParty(-9);
            var low = flow.PartySize;
            Report("party_clamp_abyss", high == 4 && low == 1);

            // 未列出来的副本选不动，仍停在原来那张上。
            flow.Choose("dragon");
            Report("party_unknown_kept", flow.ChosenId == "abyss");
        }

        // ------------------------------------------------------------------ C02

        static void CheckSettle(UiDungeonFlow flow)
        {
            Report("settle_initial", flow.ScreenSettleState.Contains("未 结 算"));

            flow.SetSettlement(true, "txn-u3-0001", 120);
            Report("settle_cleared", flow.ScreenSettleState.Contains("通 关")
                && !flow.ScreenSettleState.Contains("未 通 关"));
            Report("settle_exp_from_reply", flow.ScreenSettleExp.Contains("120"));
            Report("settle_txn_shown", flow.ScreenSettleDrop.Contains("txn-u3-0001"));

            flow.SetSettlement(false, "txn-u3-0002", 0);
            Report("settle_failed", flow.ScreenSettleState.Contains("未 通 关"));
        }

        // ------------------------------------------------------------------ C04

        static void CheckParty(int members, UiDungeonFlow flow)
        {
            // 名单没到就写「还没到」，不拿自己一个人凑一队。
            Report("party_empty", members == 0);

            // 屏一律走 ShowPage 建：直接往内容区里加控件会被记成骨架件，
            // 下一次清屏就留下悬空引用（切皮肤时会炸）。
            var shell = UiShell.Ensure();
            var four = 0;
            flow.SetParty(new List<UiDungeonFlow.PartyRow>
            {
                new UiDungeonFlow.PartyRow { Name = "镇岳", ClassId = "tank", Hp = 4200, HpMax = 4200 },
                new UiDungeonFlow.PartyRow { Name = "顾长息", ClassId = "warrior", Hp = 3820, HpMax = 4200 },
                new UiDungeonFlow.PartyRow { Name = "阿弦", ClassId = "priest", Hp = 0, HpMax = 3800, Down = true },
                new UiDungeonFlow.PartyRow { Name = "阮青", ClassId = "archer", Hp = 2100, HpMax = 3500 },
            });
            shell.ShowPage("小队", s => { four = UiPartyScreen.Build(s, flow); });
            Report("party_four", four == 4);
        }

        // ------------------------------------------------------------------ 双风格

        static void CheckSkinKeepsCopy(UiShell shell, UiDungeonFlow flow)
        {
            shell.ShowPage("裂隙", s => UiDungeonScreen.Build(s, flow));
            var light = flow.ScreenResultText + "|" + flow.ScreenHintText + "|" + flow.ScreenSizeText;

            shell.SetSkin(UiSkin.Dark, false);
            var dark = flow.ScreenResultText + "|" + flow.ScreenHintText + "|" + flow.ScreenSizeText;
            Report("skin_copy", light == dark);

            shell.SetSkin(UiSkin.Light, false);
        }

        static void Report(string key, bool ok)
        {
            Debug.Log("u3_self_" + key + "=" + (ok ? "ok" : "fail"));
            if (!ok)
            {
                _failed = true;
            }
        }
    }
}
#endif

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using Mmo.Net;
using Mmo.Proto.Battle;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U7 五屏自检：名册与席位只认回包、没回包写「尚未请求」、城战只按 SiegeView 翻译、
    /// 归属只读入口回包、家园与图鉴都接了回包（摆放 / 种植 / 图鉴各认自己的入口）、
    /// 切皮肤只换颜色。
    /// </summary>
    public static class UiSocialSelfCheck
    {
        static bool _failed;

        static int _createCalls;

        static int _bossCalls;

        public static bool Run()
        {
            _failed = false;
            _createCalls = 0;
            _bossCalls = 0;

            UiSkinStore.Reset();
            var shell = UiShell.Ensure();
            var flow = UiSocialFlow.Ensure(shell);

            // 假回包：两份建宗回包数字不同，用来证明屏上的等级 / 人数 / 上限只跟回包走。
            flow.SetSender((path, body, token, done) =>
            {
                if (path.IndexOf("/sect/create", StringComparison.Ordinal) >= 0)
                {
                    _createCalls++;
                    done(200, _createCalls == 1 ? SectSmallJson : SectLargeJson);
                    return;
                }

                if (path.IndexOf("/sect/upgrade", StringComparison.Ordinal) >= 0)
                {
                    done(200, UpgradeJson);
                    return;
                }

                if (path.IndexOf("/sect/appoint", StringComparison.Ordinal) >= 0
                    || path.IndexOf("/sect/dismiss", StringComparison.Ordinal) >= 0)
                {
                    done(200, PositionJson);
                    return;
                }

                if (path.IndexOf("/sect/transfer", StringComparison.Ordinal) >= 0
                    || path.IndexOf("/sect/leave", StringComparison.Ordinal) >= 0)
                {
                    done(200, LeadershipJson);
                    return;
                }

                if (path.IndexOf("/world/worldboss", StringComparison.Ordinal) >= 0)
                {
                    _bossCalls++;
                    done(200, _bossCalls == 1 ? BossOwnedJson : BossEmptyJson);
                    return;
                }

                done(200, SectSmallJson);
            });

            flow.Bind("selftest-token", 9);

            // 一条请求都没发过：屏上写「尚未请求」，不拿配置顶一个数上去。
            shell.ShowPage("宗门", s => UiSectScreen.Build(s, flow));
            var empty = Text(shell);
            Report("pending", empty.Contains("尚未请求") && !empty.Contains("第 "));
            Debug.Log("u7_self_pending_text=" + (empty.Contains("尚未请求") ? "尚未请求" : "missing"));

            // 第一份回包：3 级 / 17 人 / 上限 20 / 长老席位 2 之于 3。
            flow.Create(UiSocialCodex.DemoSectName);
            flow.Appoint(flow.SectId, 5, SectGuild.Elder);
            flow.Upgrade(flow.SectId);
            var sect = flow.LastSect;
            Report("facts_sect", sect.Ok && sect.Level == 3 && sect.MemberCap == 20 && sect.MemberCount == 17);
            var seat = flow.LastPosition;
            Report("facts_seat", seat.Ok && seat.SeatCount == 2 && seat.SeatCap == 3);

            shell.ShowPage("宗门", s => UiSectScreen.Build(s, flow));
            var first = Text(shell);
            Report("facts", first.Contains("第 3 级") && first.Contains("17 / 20")
                && first.Contains("elder · 2 / 3") && first.Contains("900 → 500（扣 400）"));
            Debug.Log("u7_self_facts_text=" + (first.Contains("17 / 20") ? "17 / 20" : "missing"));

            // 第二份回包数字全变：屏上要跟着变，说明没有写死的上限。
            flow.Create(UiSocialCodex.DemoSectName);
            var large = flow.LastSect;
            Report("facts_second", large.Ok && large.Level == 4 && large.MemberCap == 26
                && large.MemberCount == 21);
            shell.ShowPage("宗门", s => UiSectScreen.Build(s, flow));
            var second = Text(shell);
            Report("no_hardcoded", second.Contains("第 4 级") && second.Contains("21 / 26")
                && !second.Contains("第 3 级"));
            Debug.Log("u7_self_second_text=" + (second.Contains("21 / 26") ? "21 / 26" : "missing"));

            // 城战同屏：两个完整 + 一个未识别档位（按最省的一档处理）。
            var view = new SiegeView { FullModelLimit = 2, FullModelCount = 2, UnitCount = 3 };
            view.Units.Add(Unit(11, 1, 900, 1000, SiegeVisualTier.SiegeVisualFull));
            view.Units.Add(Unit(12, 2, 800, 1000, SiegeVisualTier.SiegeVisualFull));
            view.Units.Add(Unit(13, 3, 700, 1000, (SiegeVisualTier)9));
            flow.ApplySiegeView(view);

            var rows = flow.SiegeRows;
            var tiersOk = rows.Count == 3
                && rows[0].Visual.FullModel && rows[0].Visual.NamePlate
                && rows[1].Visual.FullModel
                && rows[2].Visual.Kind == SiegeVisualKind.MarkerOnly
                && !rows[2].Visual.FullModel && !rows[2].Visual.NamePlate
                && rows[2].Visual.TeamColor && rows[2].Visual.HpBar;
            Report("siege_tiers", tiersOk);
            Report("siege_markers", flow.Markers().Count == 1 && flow.SiegeFullCount == 2
                && flow.SiegeFullLimit == 2 && flow.SiegeUnitCount == 3);
            Debug.Log("u7_self_siege_rows=" + rows.Count + " markers=" + flow.Markers().Count);

            shell.ShowPage("领地城战", s => UiSiegeScreen.Build(s, flow));
            var siegeText = Text(shell);
            Report("siege_screen", siegeText.Contains("2 / 上限 2")
                && siegeText.Contains("1 个 · 只留队伍颜色和血条"));

            // 归属：有名字就照抄回包，没名字写「无人记名」。
            flow.LoadBoss();
            Report("owner", WorldBossEntry.OwnerLine(flow.LastBoss) == "云锦"
                && flow.LastBoss.Cap == 20 && flow.LastBoss.Visible);
            flow.LoadBoss();
            Report("owner_empty", WorldBossEntry.OwnerLine(flow.LastBoss) == "无人记名");
            shell.ShowPage("领地城战", s => UiSiegeScreen.Build(s, flow));
            Report("owner_screen", Text(shell).Contains("无人记名"));
            Debug.Log("u7_self_owner=" + WorldBossEntry.OwnerLine(flow.LastBoss));

            // 五屏都能建起来。
            var built = 0;
            built += Build(shell, flow, "宗门", (s, f) => UiSectScreen.Build(s, f));
            built += Build(shell, flow, "领地城战", (s, f) => UiSiegeScreen.Build(s, f));
            built += Build(shell, flow, "竞技场与战场", (s, f) => UiArenaScreen.Build(s, f));
            built += Build(shell, flow, "家园洞府", (s, f) => UiHomesteadScreen.Build(s, f));
            built += Build(shell, flow, "英灵图鉴", (s, f) => UiHeroCodexScreen.Build(s, f));
            Report("five", built == 5);
            Debug.Log("u7_self_five=" + built);

            // 家园：U9 接了摆放、U10 接了种植与家园币；图鉴：U11 接了 /hero-codex；
            // 竞技场：U12 接了 /versus/*。这里只认「接线后的入口在不在」，不写已接 / 未接这种会过期的话。
            shell.ShowPage("家园洞府", s => UiHomesteadScreen.Build(s, flow));
            var home = Text(shell);
            shell.ShowPage("英灵图鉴", s => UiHeroCodexScreen.Build(s, flow));
            var codex = Text(shell);
            shell.ShowPage("竞技场与战场", s => UiArenaScreen.Build(s, flow));
            var arena = Text(shell);
            Report("wired", home.Contains("读取种植") && codex.Contains("读取图鉴")
                && arena.Contains("进竞技场") && arena.Contains("B 队"));
            Debug.Log("u7_self_wired home_planting=" + home.Contains("读取种植")
                + " codex=" + codex.Contains("读取图鉴")
                + " arena=" + arena.Contains("进竞技场"));

            // 切皮肤只换颜色：布局与文案一字不差。
            shell.ShowPage("宗门", s => UiSectScreen.Build(s, flow));
            var layout = Layout(shell);
            var copy = Text(shell);
            shell.SetSkin(UiSkin.Dark, false);
            Report("skin_layout", layout == Layout(shell));
            Report("skin_copy", copy == Text(shell));
            shell.SetSkin(UiSkin.Light, false);

            shell.ClosePage();
            UiSkinStore.Reset();
            UnityEngine.Object.DestroyImmediate(flow.gameObject);
            UnityEngine.Object.DestroyImmediate(shell.gameObject);

            Debug.Log("u7_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        static SiegeUnitVisual Unit(ulong memberId, uint team, long hp, long hpMax, SiegeVisualTier tier)
        {
            return new SiegeUnitVisual
            {
                MemberId = memberId,
                Team = team,
                Hp = hp,
                HpMax = hpMax,
                Tier = tier,
            };
        }

        static int Build(UiShell shell, UiSocialFlow flow, string title, Action<UiShell, UiSocialFlow> build)
        {
            try
            {
                shell.ShowPage(title, s => build(s, flow));
                return 1;
            }
            catch (Exception error)
            {
                Report("build_" + title, false);
                Debug.LogError("u7_self_build_error " + title + " " + error);
                return 0;
            }
        }

        static void Report(string key, bool ok)
        {
            Debug.Log("u7_self_" + key + "=" + (ok ? "ok" : "fail"));
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

        static string Text(UiShell shell)
        {
            var builder = new StringBuilder();
            var labels = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                builder.Append(labels[i].name).Append(':').Append(labels[i].text).Append(';');
            }

            return builder.ToString();
        }

        const string SectSmallJson =
            "{\"ok\":true,\"reason\":\"\",\"sect_id\":7,\"name\":\"云锦\",\"level\":3," +
            "\"member_cap\":20,\"member_count\":17,\"txn_id\":\"t7a\"}";

        const string SectLargeJson =
            "{\"ok\":true,\"reason\":\"\",\"sect_id\":7,\"name\":\"云锦\",\"level\":4," +
            "\"member_cap\":26,\"member_count\":21,\"txn_id\":\"t7b\"}";

        const string UpgradeJson =
            "{\"ok\":true,\"reason\":\"\",\"sect_id\":7,\"old_level\":3,\"new_level\":4," +
            "\"member_cap\":26,\"construction_before\":900,\"construction_after\":500," +
            "\"upgrade_cost\":400,\"txn_id\":\"t7c\"}";

        const string PositionJson =
            "{\"ok\":true,\"reason\":\"\",\"sect_id\":7,\"position\":\"elder\"," +
            "\"seat_cap\":3,\"seat_count\":2,\"txn_id\":\"t7d\"}";

        const string LeadershipJson =
            "{\"ok\":true,\"reason\":\"\",\"sect_id\":7,\"old_master\":1,\"new_master\":5," +
            "\"master_count\":1,\"vice_master_count\":2,\"member_count\":17,\"txn_id\":\"t7e\"}";

        const string BossOwnedJson =
            "{\"visible\":true,\"reason\":\"\",\"entrance_id\":\"wb_1\",\"entrance_name\":\"荒原祭坛\"," +
            "\"window_key\":\"w1\",\"cap\":20,\"killed\":false,\"owner_sect_id\":7," +
            "\"owner_sect_name\":\"云锦\",\"starts_at_ms\":0,\"ends_at_ms\":0," +
            "\"next_starts_at_ms\":0}";

        const string BossEmptyJson =
            "{\"visible\":true,\"reason\":\"\",\"entrance_id\":\"wb_1\",\"entrance_name\":\"荒原祭坛\"," +
            "\"window_key\":\"w1\",\"cap\":20,\"killed\":false,\"owner_sect_id\":0," +
            "\"owner_sect_name\":\"\",\"starts_at_ms\":0,\"ends_at_ms\":0,\"next_starts_at_ms\":0}";
    }
}
#endif

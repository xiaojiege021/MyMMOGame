using System;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U8 界面入口枢纽：把 HUD 七个功能格接到已有 U 屏，并为 U5 / U6 / U7 补二级入口。
    ///
    /// 只做导航，不改玩法、不读 config、不新建权威。家园 / 图鉴 / 竞技场与战场各屏
    /// 自己接已有的 HTTP（U9–U12），这一层只负责把屏打开。
    /// </summary>
    public static class UiEntryHub
    {
        /// <summary>这一层建好的日志行。</summary>
        public const string ReadyLog = "u8_entry_hub=ready";

        static string _token = string.Empty;

        static ulong _characterId;

        /// <summary>最近一次绑定的令牌。结算屏返回表世界时用，不另存一份权威。</summary>
        public static string Token => _token;

        /// <summary>最近一次绑定的角色号。</summary>
        public static ulong CharacterId => _characterId;

        /// <summary>记下会话，供各 Flow.Bind / Configure 共用。</summary>
        public static void BindSession(string token, ulong characterId)
        {
            _token = token ?? string.Empty;
            _characterId = characterId;
        }

        /// <summary>
        /// 载入屏出口：真机玩法下问一次世界服进分线；有回包才让入口流程 Confirm。
        /// 编辑器自检不走这里（Application.isPlaying 为 false），仍由自检直接 Confirm。
        /// </summary>
        public static void GateFromLoading(UiEntryFlow entry)
        {
            if (entry == null || entry.Current != UiEntryFlow.Step.Loading)
            {
                return;
            }

            var shell = entry.Shell;
            if (shell == null)
            {
                entry.ConfirmServerEntry(false, "no_shell");
                return;
            }

            BindSession(entry.Token, entry.Character.CharacterId);
            BindFlows(shell);

            var world = UiWorldFlow.Ensure(shell);
            world.Configure(_token, _characterId);
            var region = FirstOpenRegionId();
            Debug.Log(ReadyLog + " gate region=" + region + " character=" + _characterId);
            world.SubmitEnter(region, () =>
            {
                if (!world.HasEnter)
                {
                    entry.ConfirmServerEntry(false, "network");
                    return;
                }

                // 进没进分线由表世界 HUD 显示；这里只认「服务端答了」。
                entry.ConfirmServerEntry(true, world.Enter.Reason);
            });
        }

        /// <summary>服务端确认后进入表世界 HUD，并绑好各线 Flow。</summary>
        public static void EnterWorld(UiShell shell)
        {
            if (shell == null)
            {
                return;
            }

            BindFlows(shell);
            var world = UiWorldFlow.Ensure(shell);
            world.Configure(_token, _characterId);
            shell.ShowPage(UiStrings.UiEntryHub.T001, s => UiWorldHudScreen.Build(s, world));
            Debug.Log(ReadyLog + " world character=" + _characterId);
        }

        /// <summary>任意屏回到表世界（结算「返回表世界」等）。</summary>
        public static void BackToWorld(UiShell shell)
        {
            EnterWorld(shell);
        }

        /// <summary>HUD 七格：按索引开对应主屏，并带上该线的二级入口。</summary>
        public static void OpenHudFunction(UiShell shell, UiWorldFlow world, int index)
        {
            if (shell == null)
            {
                return;
            }

            if (world != null)
            {
                world.Configure(_token, _characterId);
            }

            BindFlows(shell);
            switch (index)
            {
                case 0:
                    OpenUpgrade(shell, "character");
                    break;
                case 1:
                    OpenUpgrade(shell, "bag");
                    break;
                case 2:
                    OpenUpgrade(shell, "skill");
                    break;
                case 3:
                    OpenSocial(shell, "sect");
                    break;
                case 4:
                    OpenCommerce(shell, "shop");
                    break;
                case 5:
                    OpenDungeon(shell, "dungeon");
                    break;
                case 6:
                    shell.ShowPage(UiStrings.UiEntryHub.T002, s =>
                    {
                        DrawWorldNav(s);
                        UiWorldMapScreen.Build(s, world ?? UiWorldFlow.Ensure(s));
                    });
                    break;
                default:
                    shell.Toast(UiStrings.UiEntryHub.T003);
                    break;
            }
        }

        // ------------------------------------------------------------------ 各线打开

        public static void OpenUpgrade(UiShell shell, string which)
        {
            var flow = UiUpgradeFlow.Ensure(shell);
            flow.Bind(_token, _characterId);
            switch (which)
            {
                case "bag":
                    shell.ShowPage(UiStrings.UiEntryHub.T004, s => { DrawUpgradeNav(s, "bag"); UiBagScreen.Build(s, flow); });
                    break;
                case "enhance":
                    shell.ShowPage(UiStrings.UiEntryHub.T005, s => { DrawUpgradeNav(s, "enhance"); UiEnhanceScreen.Build(s, flow); });
                    break;
                case "transfer":
                    shell.ShowPage(UiStrings.UiEntryHub.T006, s => { DrawUpgradeNav(s, "transfer"); UiTransferScreen.Build(s, flow); });
                    break;
                case "crystal":
                    shell.ShowPage(UiStrings.UiEntryHub.T007, s => { DrawUpgradeNav(s, "crystal"); UiCrystalScreen.Build(s, flow); });
                    break;
                case "skill":
                    shell.ShowPage(UiStrings.UiEntryHub.T008, s => { DrawUpgradeNav(s, "skill"); UiSkillScreen.Build(s, flow); });
                    break;
                case "realm":
                    shell.ShowPage(UiStrings.UiEntryHub.T009, s => { DrawUpgradeNav(s, "realm"); UiRealmScreen.Build(s, flow); });
                    break;
                case "attr":
                    shell.ShowPage(UiStrings.UiEntryHub.T043, s => { DrawUpgradeNav(s, "attr"); UiAttrScreen.Build(s, flow); });
                    break;
                default:
                    shell.ShowPage(UiStrings.UiEntryHub.T010, s => { DrawUpgradeNav(s, "character"); UiCharacterScreen.Build(s, flow); });
                    break;
            }
        }

        public static void OpenCommerce(UiShell shell, string which)
        {
            var flow = UiCommerceFlow.Ensure(shell);
            flow.Bind(_token, _characterId);
            switch (which)
            {
                case "auction":
                    shell.ShowPage(UiStrings.UiEntryHub.T011, s => { DrawCommerceNav(s, "auction"); UiAuctionScreen.Build(s, flow); });
                    break;
                case "currency":
                    shell.ShowPage(UiStrings.UiEntryHub.T012, s => { DrawCommerceNav(s, "currency"); UiCurrencyScreen.Build(s, flow); });
                    break;
                case "pass":
                    shell.ShowPage(UiStrings.UiEntryHub.T013, s => { DrawCommerceNav(s, "pass"); UiPassScreen.Build(s, flow); });
                    break;
                default:
                    shell.ShowPage(UiStrings.UiEntryHub.T014, s => { DrawCommerceNav(s, "shop"); UiShopScreen.Build(s, flow); });
                    break;
            }
        }

        public static void OpenSocial(UiShell shell, string which)
        {
            var flow = UiSocialFlow.Ensure(shell);
            flow.Bind(_token, _characterId);
            switch (which)
            {
                case "siege":
                    shell.ShowPage(UiStrings.UiEntryHub.T015, s => { DrawSocialNav(s, "siege"); UiSiegeScreen.Build(s, flow); });
                    break;
                case "arena":
                    shell.ShowPage(UiStrings.UiEntryHub.T016, s => { DrawSocialNav(s, "arena"); UiArenaScreen.Build(s, flow); });
                    break;
                case "homestead":
                    shell.ShowPage(UiStrings.UiEntryHub.T017, s => { DrawSocialNav(s, "homestead"); UiHomesteadScreen.Build(s, flow); });
                    break;
                case "codex":
                    shell.ShowPage(UiStrings.UiEntryHub.T018, s => { DrawSocialNav(s, "codex"); UiHeroCodexScreen.Build(s, flow); });
                    break;
                case "party":
                {
                    var partyFlow = UiPartyFlow.Ensure(shell);
                    partyFlow.Bind(_token, _characterId);
                    shell.ShowPage(UiStrings.UiEntryHub.T019, s => { DrawSocialNav(s, "party"); UiPartyRoomScreen.Build(s, partyFlow); });
                    break;
                }
                case "boss":
                    shell.ShowPage(UiStrings.UiEntryHub.T020, s => { DrawSocialNav(s, "boss"); UiWorldBossScreen.Build(s, flow); });
                    break;
                default:
                    shell.ShowPage(UiStrings.UiEntryHub.T021, s => { DrawSocialNav(s, "sect"); UiSectScreen.Build(s, flow); });
                    break;
            }
        }

        public static void OpenDungeon(UiShell shell, string which)
        {
            var flow = UiDungeonFlow.Ensure(shell);
            flow.Configure(_token, _characterId);
            switch (which)
            {
                case "party":
                    shell.ShowPage(UiStrings.UiEntryHub.T022, s => { DrawDungeonNav(s, "party"); UiPartyScreen.Build(s, flow); });
                    break;
                case "settle":
                    shell.ShowPage(UiStrings.UiEntryHub.T023, s => { DrawDungeonNav(s, "settle"); UiSettleScreen.Build(s, flow); });
                    break;
                case "loot":
                {
                    var loot = UiLootFlow.Ensure();
                    shell.ShowPage(UiStrings.UiEntryHub.T024, s => { DrawDungeonNav(s, "loot"); UiLootScreen.Build(s, loot); });
                    break;
                }
                case "reconnect":
                {
                    var back = UiReconnectFlow.Ensure();
                    shell.ShowPage(UiStrings.UiEntryHub.T025, s => { DrawDungeonNav(s, "reconnect"); UiReconnectScreen.Build(s, back); });
                    break;
                }
                default:
                    shell.ShowPage(UiStrings.UiEntryHub.T026, s => { DrawDungeonNav(s, "dungeon"); UiDungeonScreen.Build(s, flow); });
                    break;
            }
        }

        // ------------------------------------------------------------------ 二级入口条

        static void DrawUpgradeNav(UiShell shell, string current)
        {
            DrawNav(shell, new[]
            {
                (UiStrings.UiEntryHub.T027, "character"), (UiStrings.UiEntryHub.T004, "bag"), (UiStrings.UiEntryHub.T005, "enhance"),
                (UiStrings.UiEntryHub.T028, "transfer"), (UiStrings.UiEntryHub.T029, "crystal"), (UiStrings.UiEntryHub.T030, "skill"),
                (UiStrings.UiEntryHub.T031, "realm"), (UiStrings.UiEntryHub.T043, "attr"),
                (UiStrings.UiEntryHub.T032, "world"),
            }, current, which =>
            {
                if (which == "world")
                {
                    BackToWorld(shell);
                    return;
                }

                OpenUpgrade(shell, which);
            });
        }

        static void DrawCommerceNav(UiShell shell, string current)
        {
            DrawNav(shell, new[]
            {
                (UiStrings.UiEntryHub.T014, "shop"), (UiStrings.UiEntryHub.T033, "auction"), (UiStrings.UiEntryHub.T012, "currency"),
                (UiStrings.UiEntryHub.T034, "pass"), (UiStrings.UiEntryHub.T032, "world"),
            }, current, which =>
            {
                if (which == "world")
                {
                    BackToWorld(shell);
                    return;
                }

                OpenCommerce(shell, which);
            });
        }

        static void DrawSocialNav(UiShell shell, string current)
        {
            DrawNav(shell, new[]
            {
                (UiStrings.UiEntryHub.T021, "sect"), (UiStrings.UiEntryHub.T035, "siege"), (UiStrings.UiEntryHub.T036, "arena"),
                (UiStrings.UiEntryHub.T037, "homestead"), (UiStrings.UiEntryHub.T038, "codex"), (UiStrings.UiEntryHub.T019, "party"),
                ("Boss", "boss"), (UiStrings.UiEntryHub.T032, "world"),
            }, current, which =>
            {
                if (which == "world")
                {
                    BackToWorld(shell);
                    return;
                }

                OpenSocial(shell, which);
            });
        }

        static void DrawDungeonNav(UiShell shell, string current)
        {
            DrawNav(shell, new[]
            {
                (UiStrings.UiEntryHub.T026, "dungeon"), (UiStrings.UiEntryHub.T022, "party"), (UiStrings.UiEntryHub.T039, "loot"),
                (UiStrings.UiEntryHub.T040, "reconnect"), (UiStrings.UiEntryHub.T041, "settle"), (UiStrings.UiEntryHub.T032, "world"),
            }, current, which =>
            {
                if (which == "world")
                {
                    BackToWorld(shell);
                    return;
                }

                OpenDungeon(shell, which);
            });
        }

        static void DrawWorldNav(UiShell shell)
        {
            DrawNav(shell, new[] { (UiStrings.UiEntryHub.T042, "world") }, "world", which => BackToWorld(shell));
        }

        static void DrawNav(UiShell shell, (string Label, string Id)[] links, string current, Action<string> onPick)
        {
            var m = shell.Metrics;
            var pad = m.Px(56f);
            var top = m.Px(128f);
            var h = m.Px(56f);
            var gap = m.Px(10f);
            var count = links.Length;
            var wide = m.Width - pad * 2f - gap * (count - 1);
            var w = wide / count;
            for (var i = 0; i < count; i++)
            {
                var id = links[i].Id;
                var kind = id == current ? UiButtonKind.Primary : UiButtonKind.Quiet;
                shell.Button(m.Band(pad + i * (w + gap), top, w, h), links[i].Label,
                    () => onPick(id), kind);
            }
        }

        // ------------------------------------------------------------------ 装配

        static void BindFlows(UiShell shell)
        {
            UiUpgradeFlow.Ensure(shell).Bind(_token, _characterId);
            UiCommerceFlow.Ensure(shell).Bind(_token, _characterId);
            UiSocialFlow.Ensure(shell).Bind(_token, _characterId);
            UiDungeonFlow.Ensure(shell).Configure(_token, _characterId);
            UiWorldFlow.Ensure(shell).Configure(_token, _characterId);
        }

        static string FirstOpenRegionId()
        {
            for (var i = 0; i < UiWorldCodex.Regions.Length; i++)
            {
                if (UiWorldCodex.Regions[i].Open)
                {
                    return UiWorldCodex.Regions[i].Id;
                }
            }

            return UiContentTable.Regions[0].Id;
        }
    }
}

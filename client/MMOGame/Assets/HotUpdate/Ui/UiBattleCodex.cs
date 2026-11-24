namespace HotUpdate.Ui
{
    /// <summary>
    /// 战斗与副本四屏（C01–C04）的展示数据唯一来源。
    ///
    /// 这里只放**编号、名字和口径文案**，一个数值判定都不做：
    /// 能不能进、扣多少疲劳、占多少份额、每人几份，全部来自回包。
    /// 表里的数字与 `config/dungeons.json`、`config/exp.json` 逐条对得上，
    /// `check-u3.sh` 会拿配置逐项比对，任何一边改了都会对不上。
    ///
    /// 配置里还没有的副本（龙巢、世界 Boss、宗门秘境）不在这张表里，
    /// 屏上只写一句「后续卡」，不编数据、不留假页签。
    /// </summary>
    public static class UiBattleCodex
    {
        /// <summary>一条副本的展示口径。数字与布尔值都照抄配置表。</summary>
        public struct Dungeon
        {
            public string Id;
            public string Name;
            public int MaxPlayers;
            public bool ChargesFatigue;
            public bool TakesWeeklyShare;
            public string OneLine;
            public string Tag;
        }

        /// <summary>与 `config/dungeons.json` 的 dungeons 数组同序同数。</summary>
        public static readonly Dungeon[] Dungeons =
        {
            new Dungeon
            {
                Id = "main",
                Name = UiStrings.UiBattleCodex.T001,
                MaxPlayers = 1,
                ChargesFatigue = false,
                TakesWeeklyShare = false,
                OneLine = UiStrings.UiBattleCodex.T002,
                Tag = UiStrings.UiBattleCodex.T003,
            },
            new Dungeon
            {
                Id = "abyss",
                Name = UiStrings.UiBattleCodex.T004,
                MaxPlayers = 4,
                ChargesFatigue = true,
                TakesWeeklyShare = false,
                OneLine = UiStrings.UiBattleCodex.T005,
                Tag = UiStrings.UiBattleCodex.T006,
            },
            new Dungeon
            {
                Id = "lair",
                Name = UiStrings.UiBattleCodex.T007,
                MaxPlayers = 4,
                ChargesFatigue = false,
                TakesWeeklyShare = true,
                OneLine = UiStrings.UiBattleCodex.T008,
                Tag = UiStrings.UiBattleCodex.T009,
            },
        };

        /// <summary>页签顺序＝表里顺序，不加配置里没有的副本。</summary>
        public static readonly string[] Tabs = { UiStrings.UiBattleCodex.T001, UiStrings.UiBattleCodex.T004, UiStrings.UiBattleCodex.T007 };

        // 下面这几个数字是 `config/dungeons.json` 与 `config/exp.json` 的顶格值，
        // 照抄在这里只为了正面写口径；界面判定一律用回包里的同名字段。
        public const long FatigueDailyCap = 6000;
        public const long HotSpringRecovery = 1500;
        public const int WeeklyShareCap = 12;
        public const int PartyBonusPct = 10;
        public const int PartyMinSize = 2;
        public const int PartyMaxSize = 8;
        public const int LevelCap = 100;

        /// <summary>配置里还没有的副本。只写名字，不当页签、不显示可点。</summary>
        public const string NotYet = UiStrings.UiBattleCodex.T010;
        public const string NotYetNote = UiStrings.UiBattleCodex.T011;

        /// <summary>C01 顶部 Boss 条的固定文案。阶段名与序号来自回包，不在这里。</summary>
        public const string BossBarTitle = UiStrings.UiBattleCodex.T012;
        public const string BossBarNote = UiStrings.UiBattleCodex.T013;

        /// <summary>C01 左下战斗日志的三行说明，写的是这张卡的口径，不是实时日志。</summary>
        public static readonly string[] HudNotes =
        {
            UiStrings.UiBattleCodex.T014,
            UiStrings.UiBattleCodex.T015,
            UiStrings.UiBattleCodex.T016,
        };

        /// <summary>C01 右下功能条。只标名字，点了做什么归后续卡。</summary>
        public static readonly string[] HudFunctions = { UiStrings.UiBattleCodex.T017, UiStrings.UiBattleCodex.T018, UiStrings.UiBattleCodex.T019, UiStrings.UiBattleCodex.T020 };

        /// <summary>C02 的三块口径。</summary>
        public const string SettleExpNote = UiStrings.UiBattleCodex.T021;
        public const string SettleDropNote = UiStrings.UiBattleCodex.T022;
        public const string SettleCostNote = UiStrings.UiBattleCodex.T023;

        /// <summary>C04 的三处加成与一条红线，照抄方案 11 与 exp.json 的口径。</summary>
        public static readonly string[] ExpSources =
        {
            UiStrings.UiBattleCodex.T024,
            UiStrings.UiBattleCodex.T025,
            UiStrings.UiBattleCodex.T026,
        };

        public const string PartyRule = UiStrings.UiBattleCodex.T027;

        /// <summary>按编号找一条。找不到返回 false，界面照「未开放」处理，不当成能进。</summary>
        public static bool FindDungeon(string id, out Dungeon dungeon)
        {
            for (var i = 0; i < Dungeons.Length; i++)
            {
                if (Dungeons[i].Id == id)
                {
                    dungeon = Dungeons[i];
                    return true;
                }
            }

            dungeon = default(Dungeon);
            return false;
        }

        /// <summary>C01 左下战斗日志的标题与首行。写的是这次回包的口径，不是编年史。</summary>
        public const string LogHeader = UiStrings.UiBattleCodex.T028;

        /// <summary>职业编号翻成屏上的称呼。取不到的照原样显示，不编一个职业出来。</summary>
        public static string ClassName(string classId)
        {
            switch (classId)
            {
                case "tank":
                    return UiStrings.UiBattleCodex.T029;
                case "warrior":
                    return UiStrings.UiBattleCodex.T030;
                case "mage":
                    return UiStrings.UiBattleCodex.T031;
                case "archer":
                    return UiStrings.UiBattleCodex.T032;
                case "priest":
                    return UiStrings.UiBattleCodex.T033;
                default:
                    return string.IsNullOrEmpty(classId) ? UiStrings.UiBattleCodex.T034 : classId;
            }
        }

        /// <summary>Boss 阶段那行。序号与名字都来自广播；总数服务端没下发，这里不猜。</summary>
        public static string PhaseLine(int phaseIndex, string phaseName)
        {
            if (string.IsNullOrEmpty(phaseName))
            {
                return BossBarNote;
            }

            return UiStrings.UiBattleCodex.T035 + (phaseIndex + 1) + " · " + phaseName + UiStrings.UiBattleCodex.T036;
        }

        /// <summary>被拒原因的展示文案。服务端给的新原因原样显示，不吞掉。</summary>
        public static string ReasonText(string reason)
        {
            switch (reason)
            {
                case "fatigue_exhausted":
                    return UiStrings.UiBattleCodex.T037;
                case "share_exhausted":
                    return UiStrings.UiBattleCodex.T038;
                case "party_size_mismatch":
                    return UiStrings.UiBattleCodex.T039;
                case "party_too_large":
                    return UiStrings.UiBattleCodex.T040;
                case "level_too_low":
                    return UiStrings.UiBattleCodex.T041;
                case "realm_locked":
                    return UiStrings.UiBattleCodex.T042;
                case "dungeon_unknown":
                    return UiStrings.UiBattleCodex.T043;
                case "refused":
                    return UiStrings.UiBattleCodex.T044;
                default:
                    return string.IsNullOrEmpty(reason) ? UiStrings.UiBattleCodex.T044 : reason;
            }
        }
    }
}

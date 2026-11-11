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
                Name = "主线",
                MaxPlayers = 1,
                ChargesFatigue = false,
                TakesWeeklyShare = false,
                OneLine = "仅 1 人 · 不扣疲劳 · 不占周份额 · 教学与系统解锁",
                Tag = "1 人",
            },
            new Dungeon
            {
                Id = "abyss",
                Name = "深渊",
                MaxPlayers = 4,
                ChargesFatigue = true,
                TakesWeeklyShare = false,
                OneLine = "1–4 人 · 疲劳按人数倍增 · 日上限 6000",
                Tag = "当前",
            },
            new Dungeon
            {
                Id = "lair",
                Name = "巢穴",
                MaxPlayers = 4,
                ChargesFatigue = false,
                TakesWeeklyShare = true,
                OneLine = "1–4 人 · 不扣疲劳 · 占周份额 · 每周 12 个单人份",
                Tag = "高级",
            },
        };

        /// <summary>页签顺序＝表里顺序，不加配置里没有的副本。</summary>
        public static readonly string[] Tabs = { "主线", "深渊", "巢穴" };

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
        public const string NotYet = "龙巢 / 世界 Boss / 宗门秘境";
        public const string NotYetNote = "这三类在副本表里还没有，等后续卡，本屏不列、不编数";

        /// <summary>C01 顶部 Boss 条的固定文案。阶段名与序号来自回包，不在这里。</summary>
        public const string BossBarTitle = "副本实例 · 深渊";
        public const string BossBarNote = "服务器按一次技能结算一次";

        /// <summary>C01 左下战斗日志的三行说明，写的是这张卡的口径，不是实时日志。</summary>
        public static readonly string[] HudNotes =
        {
            "选中目标后技能才发得出去",
            "伤害由回包写入，本地不预估",
            "前摇期间可以走位，命中前可打断",
        };

        /// <summary>C01 右下功能条。只标名字，点了做什么归后续卡。</summary>
        public static readonly string[] HudFunctions = { "切换目标", "小队", "复活卷轴", "离开副本" };

        /// <summary>C02 的三块口径。</summary>
        public const string SettleExpNote = "同一笔 SettleExp；≥2 人同场多一截，不叠加";
        public const string SettleDropNote = "掉落以角色服背包回包为准，本地不造物品";
        public const string SettleCostNote = "疲劳按人数倍增 · 日上限 6000 · 巢穴占周份额";

        /// <summary>C04 的三处加成与一条红线，照抄方案 11 与 exp.json 的口径。</summary>
        public static readonly string[] ExpSources =
        {
            "基础：副本 / 任务 / 竞技场 / 战场 / 城战 / 世界 Boss 结算",
            "组队：同一场 ≥2 人，每人少量加成",
            "同门：其中 ≥2 人同宗门再多一截（本版未做）",
        };

        public const string PartyRule = "只加等级经验：不加伤害、不加掉落、不加疲劳、不加副本次数";

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
        public const string LogHeader = "战斗日志";

        /// <summary>职业编号翻成屏上的称呼。取不到的照原样显示，不编一个职业出来。</summary>
        public static string ClassName(string classId)
        {
            switch (classId)
            {
                case "tank":
                    return "坦克";
                case "warrior":
                    return "战士";
                case "mage":
                    return "法师";
                case "archer":
                    return "射手";
                case "priest":
                    return "牧师";
                default:
                    return string.IsNullOrEmpty(classId) ? "未知" : classId;
            }
        }

        /// <summary>Boss 阶段那行。序号与名字都来自广播；总数服务端没下发，这里不猜。</summary>
        public static string PhaseLine(int phaseIndex, string phaseName)
        {
            if (string.IsNullOrEmpty(phaseName))
            {
                return BossBarNote;
            }

            return "阶段 " + (phaseIndex + 1) + " · " + phaseName + " · 读招与前摇由服务端给";
        }

        /// <summary>被拒原因的展示文案。服务端给的新原因原样显示，不吞掉。</summary>
        public static string ReasonText(string reason)
        {
            switch (reason)
            {
                case "fatigue_exhausted":
                    return "疲劳不够，今天进不去了";
                case "share_exhausted":
                    return "本周份额用完了";
                case "party_size_mismatch":
                    return "人数和战斗服对不上";
                case "party_too_large":
                    return "人数超过这张副本的上限";
                case "level_too_low":
                    return "等级不够";
                case "realm_locked":
                    return "境界没到";
                case "dungeon_unknown":
                    return "这张副本还没有开放";
                case "refused":
                    return "服务端拒绝了这次进本";
                default:
                    return string.IsNullOrEmpty(reason) ? "服务端拒绝了这次进本" : reason;
            }
        }
    }
}

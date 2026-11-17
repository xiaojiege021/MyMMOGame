using System.Collections.Generic;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 表世界五屏（B01–B05）的展示数据唯一来源。
    ///
    /// 这里只放**编号、名字、邻接和口径文案**，一个判定都不做：
    /// 能进哪块地表、分到第几线、采到什么、能不能飞，全部来自回包。
    /// 表里的每一条都与 `config/world.json`、`config/region/*.json`、
    /// `config/dialogue/*.json`、`config/flight.json` 逐条对得上；采集节点只认世界服节点表回包，
    /// `check-u4.sh` 会拿这五份配置逐项比对，任何一边改了都会对不上。
    ///
    /// 图上坐标只是原型 B02 那张示意图的位置，不是游戏坐标，也不参与档位与距离判定。
    /// </summary>
    public static class UiWorldCodex
    {
        /// <summary>一块地表的展示口径。邻接与开放照抄 `config/region/&lt;地名&gt;.json`。</summary>
        public struct Region
        {
            public string Id;
            public string Name;
            public int[] Chapters;
            public string[] Neighbors;
            public bool Open;
            public string Realm;
            public string Position;
            // 原型 B02 示意图上的百分比位置，只用来摆点。
            public float MapX;
            public float MapY;
        }

        /// <summary>八块地表，顺序与 `config/world.json` 的 regions 一致；内容由 config/ 生成。</summary>
        public static readonly Region[] Regions = UiContentTable.Regions;

        /// <summary>开口人的元数据（名字 / 出身 / 场次），由 config/ 生成。</summary>
        public struct SpeakerMeta
        {
            public string Name;
            public string Origin;
            public int LineCount;
            public int FirstChapter;
            public string FirstRegion;
        }

        /// <summary>一位 NPC 实体（K6）。站位与职能照 `config/npc.json`，名字不手写。</summary>
        public struct NpcMeta
        {
            public string Id;
            public string Name;
            public string Region;
            public string Role;
            public string Faction;
            public string DialogueId;
            public string ShopId;
            public float X;
            public float Y;
            public float Z;
            public float Facing;
        }

        /// <summary>一条定词（C5）：章节、地表、正文、边界逐字来自 `config/dialogue` 的生成表。</summary>
        public struct SpeakerLine
        {
            public int Chapter;
            public string Region;
            public string Text;
            public string Limit;
        }

        /// <summary>一位开口的人。元数据与定词都来自 `config/` 的生成表，本文件不写台词。</summary>
        public struct Speaker
        {
            public string Name;
            public string Origin;
            public int LineCount;
            public int FirstChapter;
            public string FirstRegion;
            public SpeakerLine[] Lines;

            public string Text
            {
                get { return Lines != null && Lines.Length > 0 ? Lines[0].Text : string.Empty; }
            }

            public string Limit
            {
                get { return Lines != null && Lines.Length > 0 ? Lines[0].Limit : string.Empty; }
            }

            public string SecondText
            {
                get { return Lines != null && Lines.Length > 1 ? Lines[1].Text : string.Empty; }
            }

            public string SecondLimit
            {
                get { return Lines != null && Lines.Length > 1 ? Lines[1].Limit : string.Empty; }
            }
        }

        /// <summary>九位开口的人：名字 / 出身 / 场次与全部定词都来自生成表。</summary>
        public static readonly Speaker[] Speakers = BuildSpeakers();

        /// <summary>配置里定词的总条数；屏脚注按它写，改完配置重生成后自动跟着变。</summary>
        public static readonly int TotalDialogueLines = CountDialogueLines();

        static Speaker[] BuildSpeakers()
        {
            var meta = UiContentTable.Speakers;
            var dialogue = UiContentTable.SpeakerDialogue;
            var list = new Speaker[meta.Length];
            for (var i = 0; i < meta.Length; i++)
            {
                var lines = i < dialogue.Length ? dialogue[i] : new SpeakerLine[0];
                list[i] = new Speaker
                {
                    Name = meta[i].Name,
                    Origin = meta[i].Origin,
                    LineCount = lines.Length,
                    FirstChapter = meta[i].FirstChapter,
                    FirstRegion = meta[i].FirstRegion,
                    Lines = lines,
                };
            }

            return list;
        }

        static int CountDialogueLines()
        {
            var count = 0;
            for (var i = 0; i < Speakers.Length; i++)
            {
                if (Speakers[i].Lines != null)
                {
                    count += Speakers[i].Lines.Length;
                }
            }

            return count;
        }

        /// <summary>三个采集分组。页签顺序与原型 B04 一致。</summary>
        public static readonly string[] GatherTabs = { "矿脉", "药草", "灵石矿脉" };

        /// <summary>三段大境界，顺序照 `config/flight.json` 的 realms，由生成表带过来。</summary>
        public static readonly string[] Realms = UiContentTable.Realms;

        /// <summary>能飞的起点境界，照 `config/flight.json` 的 flight_realm。</summary>
        public const string FlightRealm = UiContentTable.FlightRealm;

        /// <summary>同屏三档的展示名。档位本身来自回包的 AppearanceTier，不在这里判。</summary>
        public static readonly string[] TierLabels = { "完整模型", "简模", "只留名字" };

        // 两档距离是 `config/world.json` 的顶格值，只用来在屏上写注脚；
        // 客户端不拿它算档位，档位一律按服务端下发的 AppearanceTier 翻译。
        public const float TierNearDistance = 8f;
        public const float TierFarDistance = 20f;

        /// <summary>B02 跨区的拒绝顺序，照抄方案与原型 B02 写定的那一行。</summary>
        public const string RegionRejectOrder =
            "not_in_line → unknown_region → not_adjacent → region_locked → 读境界 → region_realm_locked → Enter";

        /// <summary>B04 采集的拒绝顺序，照抄原型 B04 写定的那一行。</summary>
        public const string GatherRejectOrder =
            "unknown_node → spirit_stone_forbidden → not_in_line → wrong_region → node_busy → 发奖；上游失败释放占用回 grant_failed";

        /// <summary>B01 的三个口径。</summary>
        public const string HudLine = "表世界不结算战斗；走动不计历练";
        public const string HudTierNote = "同屏三档（距离由服务端下发）";
        public const string HudFlightNote = "飞行只由境界打开；没有权限时拖尾也不让人飞";

        /// <summary>B01 左下「附近」三条。</summary>
        public static readonly string[] HudNearby =
        {
            Speakers[0].Name + " · 开口的人 · " + Speakers[0].FirstRegion,
            "矿脉 · 可采集 · 不耗疲劳",
            "裂隙入口 · 进副本 / 战场的唯一通道",
        };

        /// <summary>B01 右下功能条。U8 接到已有 U 屏；点了做什么在 UiEntryHub。</summary>
        public static readonly string[] HudFunctions =
        {
            "角色", "背包", "技能", "宗门", "商城", "裂隙", "世界地图",
        };

        /// <summary>B01 中间那五个操作签。</summary>
        public static readonly string[] HudActions = { "采集", "对白", "飞行（境界锁）", "切目标" };

        /// <summary>没有回包时屏上写的话。不拿配置顶格值假装已经确认。</summary>
        public const string NoEnterYet = "尚未进分线 · 分线与容量以回包为准";
        public const string NoGatherNodesYet = "尚未请求 · 节点表以回包为准";
        public const string NoGatherYet = "尚未采集 · 产物与格位以回包为准";
        public const string NoRegionYet = "尚未跨区 · 目标与门槛以回包为准";
        public const string NoFlyYet = "尚未起飞 · 境界与高度以回包为准";
        public const string NoRegionsYet = "地点表还没读到 · 先在网关取一次";

        public static bool FindRegion(string id, out Region region)
        {
            for (var i = 0; i < Regions.Length; i++)
            {
                if (Regions[i].Id == id)
                {
                    region = Regions[i];
                    return true;
                }
            }

            region = default;
            return false;
        }

        /// <summary>采集分组的展示名到世界服 kind 字段。</summary>
        public static string KindOfTab(string tab)
        {
            switch (tab)
            {
                case "药草":
                    return "herb";
                case "灵石矿脉":
                    return "spirit_stone";
                default:
                    return "ore";
            }
        }

        /// <summary>被拒原因给人看的一句话。服务端给的新原因原样带出来，不吞掉。</summary>
        public static string ReasonText(string reason)
        {
            switch (reason)
            {
                case "not_in_line":
                    return "不在那条分线里";
                case "unknown_region":
                    return "没有这块地表";
                case "not_adjacent":
                    return "这两块地表不相连";
                case "region_locked":
                    return "这块地表还没开（剧情未到）";
                case "region_realm_locked":
                    return "境界不够，进不去这块地表";
                case "unknown_node":
                    return "没有这个采集点";
                case "spirit_stone_forbidden":
                    return "灵石矿脉不开放个人采集";
                case "wrong_region":
                    return "人与采集点不在同一块地表";
                case "node_busy":
                    return "这个点正被占用";
                case "grant_failed":
                    return "入背包失败，占用已释放";
                case "flight_forbidden":
                    return "境界不够，飞不起来";
                case "realm_locked":
                    return "境界不到";
                case "too_high":
                    return "位置帧的高度超了，已拉回地面";
                case "not_entered":
                    return "还没进分线";
                case "network":
                    return "连不上网关";
                case "unavailable":
                    return "服务端暂时不可用";
                case "invalid_token":
                    return "令牌失效";
                case "":
                    return "服务端没给原因";
                default:
                    return reason;
            }
        }
    }
}

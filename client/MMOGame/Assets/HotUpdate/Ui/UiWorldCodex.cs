using System.Collections.Generic;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 表世界五屏（B01–B05）的展示数据唯一来源。
    ///
    /// 这里只放**编号、名字、邻接和口径文案**，一个判定都不做：
    /// 能进哪块地表、分到第几线、采到什么、能不能飞，全部来自回包。
    /// 表里的每一条都与 `config/world.json`、`config/region/*.json`、
    /// `config/dialogue/*.json`、`config/gather.json`、`config/flight.json` 逐条对得上，
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

        /// <summary>与 `config/world.json` 的 regions 同序同数，境界门槛取自 `config/flight.json`。</summary>
        public static readonly Region[] Regions =
        {
            new Region
            {
                Id = "枯岚道", Name = "枯岚道", Chapters = new[] { 1, 2 },
                Neighbors = new[] { "承露门" }, Open = true, Realm = "炼体",
                Position = "起点。连着承露门", MapX = 14f, MapY = 62f,
            },
            new Region
            {
                Id = "承露门", Name = "承露门", Chapters = new[] { 3 },
                Neighbors = new[] { "枯岚道", "听谣镇" }, Open = true, Realm = "炼体",
                Position = "枯岚道尽头。连着听谣镇", MapX = 30f, MapY = 54f,
            },
            new Region
            {
                Id = "听谣镇", Name = "听谣镇", Chapters = new[] { 5 },
                Neighbors = new[] { "承露门", "栖云城", "外环隙" }, Open = true, Realm = "炼体",
                Position = "承露门和栖云城之间", MapX = 46f, MapY = 66f,
            },
            new Region
            {
                Id = "外环隙", Name = "外环隙", Chapters = new[] { 6 },
                Neighbors = new[] { "听谣镇" }, Open = true, Realm = "炼体",
                Position = "听谣镇外侧的一道地表入口", MapX = 30f, MapY = 80f,
            },
            new Region
            {
                Id = "栖云城", Name = "栖云城", Chapters = new[] { 12 },
                Neighbors = new[] { "听谣镇", "旧天城区", "焦原", "龙巢门口" }, Open = true, Realm = "炼体",
                Position = "这片密区的主城。听谣镇能走到", MapX = 64f, MapY = 46f,
            },
            new Region
            {
                Id = "旧天城区", Name = "旧天城区", Chapters = new[] { 11 },
                Neighbors = new[] { "栖云城" }, Open = false, Realm = "结丹",
                Position = "从栖云城走出的短支路。高空航路下面，第 11 场才开", MapX = 82f, MapY = 34f,
            },
            new Region
            {
                Id = "焦原", Name = "焦原", Chapters = new[] { 11 },
                Neighbors = new[] { "栖云城" }, Open = false, Realm = "结丹",
                Position = "从栖云城走出的另一条短支路。第 11 场才开", MapX = 82f, MapY = 68f,
            },
            new Region
            {
                Id = "龙巢门口", Name = "龙巢门口", Chapters = new[] { 12 },
                Neighbors = new[] { "栖云城" }, Open = true, Realm = "结丹",
                Position = "栖云城外缘，能看见", MapX = 64f, MapY = 78f,
            },
        };

        /// <summary>一位开口的人。定词与边界照抄 `config/dialogue/&lt;姓名&gt;.json` 的第一场。</summary>
        public struct Speaker
        {
            public string Name;
            public string Origin;
            public int LineCount;
            public int FirstChapter;
            public string FirstRegion;
            public string Text;
            public string Limit;
            // 只有原型 B03 明确要展示第二场的那一位（乔小满）才填这两格；
            // 其余多场角色留空，屏上只写「还有几场在配置里」，不拿别人的台词顶上。
            public string SecondText;
            public string SecondLimit;
        }

        /// <summary>与 `config/world.json` 的 speakers 同序同数，九条定词逐字照抄配置。</summary>
        public static readonly Speaker[] Speakers =
        {
            new Speaker
            {
                Name = "乔小满", Origin = "尘民", LineCount = 2, FirstChapter = 1, FirstRegion = "枯岚道",
                Text = "灵气这么薄，先把眼前这只选中。选不中，后面的招式没有用。",
                Limit = "只在枯岚道教锁定。不加入后面的队伍",
                SecondText = "它要扑的那一下有前摇。你要么拉开，要么挡住。",
                SecondLimit = "送到承露门门口就离开",
            },
            new Speaker
            {
                Name = "苏晚织", Origin = "尘民", LineCount = 2, FirstChapter = 3, FirstRegion = "承露门",
                Text = "承露门只教天地玄黄。外面说秘境灵气无尽，能帮人破境。这句话是传的，门派自己没进去过。",
                Limit = "代表门派收徒，不替玩家选职业",
            },
            new Speaker
            {
                Name = "阮青", Origin = "脉裔", LineCount = 4, FirstChapter = 4, FirstRegion = "",
                Text = "我家的脉一年比一年细。我出来是找那处秘境，不是找一个救主。",
                Limit = "同行之一。四种出身都能遇见他",
            },
            new Speaker
            {
                Name = "阿弦", Origin = "岚民", LineCount = 1, FirstChapter = 4, FirstRegion = "",
                Text = "旧天路断了。我跟着走一段，走到入口就知道传闻真不真。",
                Limit = "同行之一。创角时不能飞，他也不能",
            },
            new Speaker
            {
                Name = "闻嫂", Origin = "", LineCount = 1, FirstChapter = 5, FirstRegion = "听谣镇",
                Text = "住店、听传闻，都按天算。突破的东西这里不卖。",
                Limit = "不卖灵石、丹药和装备",
            },
            new Speaker
            {
                Name = "孟七", Origin = "", LineCount = 1, FirstChapter = 8, FirstRegion = "",
                Text = "续仙盟按人头给钱。认得出承露门的衣服。我是受雇的。",
                Limit = "这场之后不再出场",
            },
            new Speaker
            {
                Name = "澄川", Origin = "", LineCount = 1, FirstChapter = 11, FirstRegion = "旧天城区",
                Text = "不顺着续仙盟走，只是不顺着他们。承露门对过的事，这里不替它认。",
                Limit = "只在旧天城区出现。不加副本次数",
            },
            new Speaker
            {
                Name = "灰生", Origin = "", LineCount = 1, FirstChapter = 11, FirstRegion = "焦原",
                Text = "用这里的力量往下走，不是去续仙盟报到。",
                Limit = "只在焦原出现。选他这条不是加入续仙盟",
            },
            new Speaker
            {
                Name = "周晚棠", Origin = "", LineCount = 1, FirstChapter = 12, FirstRegion = "栖云城",
                Text = "入口太多，这座城守不住。守护和税收包给宗门。个人的路停在龙巢门口。",
                Limit = "不要求玩家单独拯救栖云城",
            },
        };

        /// <summary>乔小满第 1、2 场的定词，字字照抄 `config/dialogue/乔小满.json`。</summary>
        public const string QiaoChapter1 = "灵气这么薄，先把眼前这只选中。选不中，后面的招式没有用。";
        public const string QiaoChapter1Limit = "只在枯岚道教锁定。不加入后面的队伍";
        public const string QiaoChapter2 = "它要扑的那一下有前摇。你要么拉开，要么挡住。";
        public const string QiaoChapter2Limit = "送到承露门门口就离开";

        /// <summary>一位采集点的展示口径。产物与数量只在配置里读，屏上算不到。</summary>
        public struct GatherNode
        {
            public string Id;
            public string Kind;
            public string Region;
            public string ItemId;
            public int Count;
            public uint BagSlot;
            public int RespawnSeconds;
            // 灵石矿脉只登记归属，禁止个人采集，配置里也没有产物与刷新。
            public bool Forbidden;
        }

        /// <summary>与 `config/gather.json` 的 nodes 同序同数。</summary>
        public static readonly GatherNode[] Nodes =
        {
            new GatherNode
            {
                Id = "ore_kulan_1", Kind = "ore", Region = "枯岚道", ItemId = "ore_iron",
                Count = 2, BagSlot = 1, RespawnSeconds = 2, Forbidden = false,
            },
            new GatherNode
            {
                Id = "ore_kulan_2", Kind = "ore", Region = "枯岚道", ItemId = "ore_iron",
                Count = 2, BagSlot = 1, RespawnSeconds = 2, Forbidden = false,
            },
            new GatherNode
            {
                Id = "herb_tingyao_1", Kind = "herb", Region = "听谣镇", ItemId = "herb_ling",
                Count = 3, BagSlot = 2, RespawnSeconds = 2, Forbidden = false,
            },
            new GatherNode
            {
                Id = "spirit_qiyun_1", Kind = "spirit_stone", Region = "栖云城",
                ItemId = "", Count = 0, BagSlot = 0, RespawnSeconds = 0, Forbidden = true,
            },
        };

        /// <summary>三个采集分组。页签顺序就是配置里出现的顺序。</summary>
        public static readonly string[] GatherTabs = { "矿脉", "药草", "灵石矿脉" };

        /// <summary>三段大境界，顺序照 `config/flight.json` 的 realms，不另起名字。</summary>
        public static readonly string[] Realms = { "炼体", "筑基", "结丹" };

        /// <summary>能飞的起点境界。数值在 `config/flight.json` 的 flight_realm，这里照抄只作说明。</summary>
        public const string FlightRealm = "筑基";

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
            "乔小满 · 开口的人 · 枯岚道",
            "矿脉 · 可采集 · 不耗疲劳",
            "裂隙入口 · 进副本 / 战场的唯一通道",
        };

        /// <summary>B01 右下功能条。只标名字，点了做什么归后续卡。</summary>
        public static readonly string[] HudFunctions =
        {
            "角色", "背包", "技能", "宗门", "商城", "裂隙", "世界地图",
        };

        /// <summary>B01 中间那五个操作签。</summary>
        public static readonly string[] HudActions = { "采集", "对白", "飞行（境界锁）", "切目标" };

        /// <summary>没有回包时屏上写的话。不拿配置顶格值假装已经确认。</summary>
        public const string NoEnterYet = "尚未进分线 · 分线与容量以回包为准";
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

        public static bool FindNode(string id, out GatherNode node)
        {
            for (var i = 0; i < Nodes.Length; i++)
            {
                if (Nodes[i].Id == id)
                {
                    node = Nodes[i];
                    return true;
                }
            }

            node = default;
            return false;
        }

        /// <summary>同属一块地表的采集点。页签切组就照这个筛。</summary>
        public static List<GatherNode> NodesOf(string kind)
        {
            var list = new List<GatherNode>();
            for (var i = 0; i < Nodes.Length; i++)
            {
                if (Nodes[i].Kind == kind)
                {
                    list.Add(Nodes[i]);
                }
            }

            return list;
        }

        /// <summary>采集分组的展示名到配置里的 kind。</summary>
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

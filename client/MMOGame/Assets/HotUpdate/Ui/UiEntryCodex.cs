using System;
using System.Collections.Generic;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 入口四屏要用的展示数据：四种出身、五个职业、幕一题记。
    ///
    /// 这里只有名字、出身对白、外观与题记，没有一条数值——出身不加属性、不锁职业
    /// （方案 8.4），职业只决定技能与职责。出身顺序与外观 JSON 和 `config/races.json`
    /// 逐字对应，职业集合与 `config/classes.json` 相同（展示顺序按原型
    /// `docs/登录界面原型.html` 的五个签），`tools/check/check-u2.sh` 逐条比对；
    /// 任一边改了都会对不上，客户端不会自己编一份。
    /// </summary>
    public static class UiEntryCodex
    {
        /// <summary>幕一标识，登入与载入两屏共用。</summary>
        public const string ActOne = "仙路已断";

        /// <summary>竖排主标题。</summary>
        public const string Brand = "长生隙";

        /// <summary>主标题右侧的副题。</summary>
        public const string BrandSub = "表里两界之间那道缝";

        /// <summary>印章。</summary>
        public const string Seal = "云锦";

        /// <summary>题记出处，静态文字，不接接口。</summary>
        public const string VerseSign = "听谣镇旧谣";

        /// <summary>登入屏固定显示的那一首（01 定稿）。</summary>
        public const string VerseFixedA = "仙路尽头谁为峰";

        /// <summary>01 定稿的下句。</summary>
        public const string VerseFixedB = "一见隙开道成空";

        /// <summary>名号上限，与原型输入框一致。</summary>
        public const int NameMaxLength = 12;

        /// <summary>一种出身。外观是给服务端原样落库的那一段 JSON。</summary>
        public struct Race
        {
            public string Id;
            public string Name;
            public string Origin;
            public string Appearance;
        }

        /// <summary>一个职业。只有编号与名字，没有属性——属性在服务端配置里。</summary>
        public struct Role
        {
            public string Id;
            public string Name;
        }

        /// <summary>四种出身，与 `config/races.json` 同序。</summary>
        public static readonly Race[] Races =
        {
            new Race
            {
                Id = "chen", Name = "尘民", Origin = "灵气稀薄之后最常见的凡人",
                Appearance = "{\"body\":\"common\",\"mark\":\"none\",\"tone\":\"sun\"}",
            },
            new Race
            {
                Id = "mai", Name = "脉裔", Origin = "住在还没干涸的灵脉旁边",
                Appearance = "{\"body\":\"slim\",\"mark\":\"jade_crack\",\"tone\":\"pale\"}",
            },
            new Race
            {
                Id = "lan", Name = "岚民", Origin = "从前走高空航路的人，航路断了以后住在崖上",
                Appearance = "{\"body\":\"tall\",\"mark\":\"none\",\"tone\":\"wind\"}",
            },
            new Race
            {
                Id = "yuan", Name = "渊户", Origin = "世代守在秘境入口旁边的人家",
                Appearance = "{\"body\":\"stout\",\"mark\":\"rope\",\"tone\":\"dark\"}",
            },
        };

        /// <summary>五个基础职业，展示顺序按原型。分支不在这里——15 级一转才选。</summary>
        public static readonly Role[] Roles =
        {
            new Role { Id = "tank", Name = "坦克" },
            new Role { Id = "warrior", Name = "战士" },
            new Role { Id = "mage", Name = "法师" },
            new Role { Id = "archer", Name = "射手" },
            new Role { Id = "priest", Name = "牧师" },
        };

        /// <summary>听谣镇旧谣 15 首，载入屏从这里抽。</summary>
        public static readonly string[][] Verses =
        {
            new[] { "仙路尽头谁为峰", "一见隙开道成空" },
            new[] { "灵气一年薄一寸", "功法一代厚一函" },
            new[] { "天地玄黄传了一代又一代", "门外旧路断了一年又一年" },
            new[] { "崖上的人不抬头", "说天上从前有航路" },
            new[] { "从前走到头的说路还在", "后来没走到的说路本来就在" },
            new[] { "听说隙里灵气满", "进去的没一个回来改口" },
            new[] { "独去的人数日子", "结伴的人数名字" },
            new[] { "带得回山里的石", "带不回身上的境" },
            new[] { "渊户世代守着门", "先等来的却是收税的人" },
            new[] { "立仙走旧天城", "立魔过焦原" },
            new[] { "秘境不生灵气", "只生收税的人" },
            new[] { "城里挂的是仙榜", "榜下站的是账房" },
            new[] { "龙巢的门一直开着", "没人问里面，只问谁守门" },
            new[] { "局是假的", "脚底下的路是真的" },
            new[] { "云海年年白，光柱年年落", "仰望的人换了几代" },
        };

        static readonly List<int> Bag = new List<int>();

        static Random _random = new Random(Environment.TickCount);

        /// <summary>洗牌袋里还剩几首。核对用它验「15 首轮完一遍才重复」。</summary>
        public static int BagLeft => Bag.Count;

        /// <summary>重置洗牌袋。种子固定时抽出来的顺序也固定，便于核对。</summary>
        public static void ResetVerseBag(int seed)
        {
            _random = new Random(seed);
            Bag.Clear();
            for (var i = 0; i < Verses.Length; i++)
            {
                Bag.Add(i);
            }
        }

        /// <summary>
        /// 抽一首载入题记。一副洗牌袋抽完再洗，所以 15 首轮完一遍才会重复，
        /// 不会连着两屏都是同一首。只作表现，不接接口、不进库。
        /// </summary>
        public static string[] NextVerse()
        {
            if (Bag.Count == 0)
            {
                ResetVerseBag(_random.Next());
            }

            var pick = _random.Next(Bag.Count);
            var index = Bag[pick];
            Bag.RemoveAt(pick);
            return Verses[index];
        }

        /// <summary>按编号找出身。找不到返回 null（界面不会拿未知编号去请求）。</summary>
        public static Race? FindRace(string id)
        {
            for (var i = 0; i < Races.Length; i++)
            {
                if (string.Equals(Races[i].Id, id, StringComparison.Ordinal))
                {
                    return Races[i];
                }
            }

            return null;
        }

        /// <summary>按编号找职业。</summary>
        public static Role? FindRole(string id)
        {
            for (var i = 0; i < Roles.Length; i++)
            {
                if (string.Equals(Roles[i].Id, id, StringComparison.Ordinal))
                {
                    return Roles[i];
                }
            }

            return null;
        }
    }
}

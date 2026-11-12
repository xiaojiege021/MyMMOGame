using System.Collections.Generic;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U5 七屏的固定文案与「只作请求参数」的档位表。
    ///
    /// 和 `UiBattleCodex` 同一个用法：这里的文字是原型与方案里写定的说明，
    /// 档位编号只用来拼请求（服务端仍按 config/enhance.json 校验，编号不对会被拒），
    /// 数值、概率、费用、绑定一条都不在这里算。
    /// </summary>
    public static class UiUpgradeCodex
    {
        /// <summary>强化三档。stone_id 只作请求参数，服务端按配置校验。</summary>
        public struct Tier
        {
            public string Id;
            public string Name;
            public string StoneId;
            public string Range;
        }

        public static readonly Tier[] Tiers =
        {
            new Tier { Id = "low", Name = "低级强化石", StoneId = "stone_enhance_low", Range = "+1 → +6" },
            new Tier { Id = "mid", Name = "中级强化石", StoneId = "stone_enhance_mid", Range = "+7 → +9" },
            new Tier { Id = "high", Name = "高级强化石", StoneId = "stone_enhance_high", Range = "+10 → +12" },
        };

        /// <summary>职业与分支：界面只列，不进请求、不产生属性。</summary>
        public static readonly string[] Classes = { "战士", "弓手", "法师", "牧师", "守护" };

        public static readonly string[] Branches =
        {
            "镇岳（守护）", "古雷（法师）", "破军（战士）", "仙真（牧师）", "穿杨（弓手）",
        };

        /// <summary>境界阶。突破接口在后面的卡，这一屏只显示角色服给的当前境界。</summary>
        public static readonly string[] Realms =
        {
            "炼体", "筑基", "金丹", "元婴", "化神", "炼虚", "合体", "大乘", "渡劫", "散仙", "金仙",
        };

        public const string EnhanceNote =
            "上限 +12 · 失败只掉 1 级、不碎、不清零 · 成功率与费用由服务端按配置算，界面只显示回包";

        public const string TransferNote =
            "换装不自动带强化 · 来源必须高于目标 · 覆盖不返还 · 绑定跟着这一笔走 · 必定成功";

        public const string CrystalNote =
            "三类 · 部位限定 · 可拆卸复用 · 装上与拆下都不改强化、金币与绑定";

        public const string SkillsNote =
            "技能与分支由服务端下发；界面只列名字，不排技能、不算伤害。";

        public const string RealmNote =
            "突破由服务端判定，本卡没有突破接口；这里只显示角色服给的当前境界。";

        /// <summary>四个装备部位（原型 D01）。</summary>
        public static readonly string[] Parts = { "兵器", "衣甲", "饰品", "结晶位" };

        /// <summary>把背包格按显示顺序取前 N 个候选。</summary>
        public static List<Mmo.Net.BagSlotView> FirstSlots(Mmo.Net.BagOutcome bag, int count)
        {
            var list = new List<Mmo.Net.BagSlotView>();
            if (bag.Slots == null)
            {
                return list;
            }

            for (var i = 0; i < bag.Slots.Count && list.Count < count; i++)
            {
                list.Add(bag.Slots[i]);
            }

            return list;
        }
    }
}

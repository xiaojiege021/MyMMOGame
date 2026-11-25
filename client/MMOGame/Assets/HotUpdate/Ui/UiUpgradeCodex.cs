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
            new Tier { Id = "low", Name = UiStrings.UiUpgradeCodex.T001, StoneId = "stone_enhance_low", Range = "+1 → +6" },
            new Tier { Id = "mid", Name = UiStrings.UiUpgradeCodex.T002, StoneId = "stone_enhance_mid", Range = "+7 → +9" },
            new Tier { Id = "high", Name = UiStrings.UiUpgradeCodex.T003, StoneId = "stone_enhance_high", Range = "+10 → +12" },
        };

        /// <summary>职业与分支：界面只列，不进请求、不产生属性。</summary>
        public static readonly string[] Classes = { UiStrings.UiUpgradeCodex.T004, UiStrings.UiUpgradeCodex.T005, UiStrings.UiUpgradeCodex.T006, UiStrings.UiUpgradeCodex.T007, UiStrings.UiUpgradeCodex.T008 };

        public static readonly string[] Branches =
        {
            UiStrings.UiUpgradeCodex.T009, UiStrings.UiUpgradeCodex.T010, UiStrings.UiUpgradeCodex.T011, UiStrings.UiUpgradeCodex.T012, UiStrings.UiUpgradeCodex.T013,
        };

        public const string EnhanceNote =
            UiStrings.UiUpgradeCodex.T014;

        public const string TransferNote =
            UiStrings.UiUpgradeCodex.T015;

        public const string CrystalNote =
            UiStrings.UiUpgradeCodex.T016;

        public const string SkillsNote =
            UiStrings.UiUpgradeCodex.T017;

        public const string RealmNote =
            UiStrings.UiUpgradeCodex.T018;

        /// <summary>
        /// A9：四属性的服务端键，顺序与回包里四个字段一致。只作请求参数，
        /// 白名单与上限都在角色服（`config/attr_points.json`），这里不判。
        /// </summary>
        public static readonly string[] Attrs = { "strength", "agility", "spirit", "vitality" };

        /// <summary>四属性的中文名，只用于把回包里的键说成人话。</summary>
        public static readonly string[] AttrNames =
        {
            UiStrings.UiUpgradeCodex.T023, UiStrings.UiUpgradeCodex.T024,
            UiStrings.UiUpgradeCodex.T025, UiStrings.UiUpgradeCodex.T026,
        };

        /// <summary>A9：属性屏顶那一行说明。</summary>
        public const string AttrsNote = UiStrings.UiUpgradeCodex.T027;

        public const string CraftNote = UiStrings.UiUpgradeCodex.T028;

        /// <summary>四个装备部位（原型 D01）。</summary>
        public static readonly string[] Parts = { UiStrings.UiUpgradeCodex.T019, UiStrings.UiUpgradeCodex.T020, UiStrings.UiUpgradeCodex.T021, UiStrings.UiUpgradeCodex.T022 };

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

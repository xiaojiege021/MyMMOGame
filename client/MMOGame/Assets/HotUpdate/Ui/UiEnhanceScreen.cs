using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 强化（原型 D03）。选一格、选一档石头、决定用不用保护券，然后等回包。
    /// 成功率、费用、等级、券的扣减全看 `/character/enhance` 回包；
    /// 被拒时把未生效的数值按回包原样显示（服务端给 0 就显示 0）。
    /// </summary>
    public static class UiEnhanceScreen
    {
        public const string ReadyLog = "u5_enhance_screen=ready";

        public static void Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell, UiUpgradeCodex.EnhanceNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.5f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;

            var chosenSlot = 0u;
            var chosenTier = 0;
            var useCoupon = new[] { false };

            // 左：可选的背包格。
            var pick = shell.Panel("选一格", m.Band(pad, top + m.Px(56f), leftW, m.Px(620f)), shell.Size(32f));
            var rows = flow.HasBag ? UiUpgradeCodex.FirstSlots(flow.Bag, 6) : new List<Mmo.Net.BagSlotView>();
            var chosenText = shell.LabelIn(pick.Root,
                new Rect(m.Px(28f), m.Px(28f), leftW - m.Px(56f), m.Px(44f)),
                rows.Count > 0 ? "已选第 " + rows[0].Slot + " 格" : "尚未读背包",
                UiTextRole.Faint, shell.Size(24f), TextAnchor.MiddleLeft, false);
            if (rows.Count > 0)
            {
                chosenSlot = rows[0].Slot;
            }

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var opt = new UiOption(
                    pick.Root,
                    new Rect(m.Px(24f), m.Px(92f) + i * m.Px(80f), leftW - m.Px(48f), m.Px(68f)),
                    row.Slot.ToString(), "第 " + row.Slot + " 格",
                    row.ItemId + " × " + row.Count + (row.Bound ? " · 绑定" : ""),
                    UiOptionKind.Card, shell.Size(26f), shell.Size(20f));
                opt.Selected = i == 0;
                var slot = row.Slot;
                opt.OnPick = picked =>
                {
                    chosenSlot = slot;
                    chosenText.Label.text = "已选第 " + slot + " 格";
                    Debug.Log("u5_enhance_pick slot=" + slot);
                };
                shell.Register(opt);
            }

            // 右：三档石头 + 券开关 + 开始。
            var actions = shell.Panel("这一手", m.Band(rightX, top + m.Px(56f), rightW, m.Px(420f)), shell.Size(32f));
            for (var i = 0; i < UiUpgradeCodex.Tiers.Length; i++)
            {
                var tier = UiUpgradeCodex.Tiers[i];
                var opt = new UiOption(
                    actions.Root,
                    new Rect(m.Px(24f), m.Px(120f) + i * m.Px(86f), rightW - m.Px(48f), m.Px(72f)),
                    tier.Id, tier.Name, tier.Range, UiOptionKind.Chip,
                    shell.Size(24f), shell.Size(20f));
                opt.Selected = i == 0;
                var index = i;
                opt.OnPick = picked =>
                {
                    chosenTier = index;
                    Debug.Log("u5_enhance_tier=" + picked.Id);
                };
                shell.Register(opt);
            }

            shell.ButtonIn(actions.Root,
                new Rect(m.Px(24f), m.Px(24f), m.Px(300f), m.Px(72f)), "不用保护券",
                () =>
                {
                    useCoupon[0] = !useCoupon[0];
                    Debug.Log("u5_enhance_coupon=" + (useCoupon[0] ? "true" : "false"));
                }, UiButtonKind.Normal);

            shell.ButtonIn(actions.Root,
                new Rect(rightW - m.Px(24f) - m.Px(260f), m.Px(24f), m.Px(260f), m.Px(72f)),
                "开始强化",
                () =>
                {
                    if (flow.Busy || chosenSlot == 0)
                    {
                        return;
                    }

                    flow.Enhance(chosenSlot, UiUpgradeCodex.Tiers[chosenTier].StoneId,
                        useCoupon[0], shell.RefreshPage);
                }, UiButtonKind.Primary);

            // 结果：只读回包。
            var result = shell.Panel("回包", m.Band(rightX, top + m.Px(56f) + m.Px(460f), rightW, m.Px(520f)),
                shell.Size(32f));
            var e = flow.LastEnhance;
            var has = flow.HasEnhance;
            UiUpgradeViews.Result(shell, result, m.Px(400f), "等级", has ? e.LevelBefore + " → " + e.Level : "尚未请求");
            UiUpgradeViews.Result(shell, result, m.Px(344f), "结果",
                has ? (e.Ok ? (e.Success ? "成功" : "失败") : "被拒：" + e.Reason) : "尚未请求");
            UiUpgradeViews.Result(shell, result, m.Px(288f), "档位 / 石头",
                has ? e.Tier + " / " + e.RequiredStone : "—");
            UiUpgradeViews.Result(shell, result, m.Px(232f), "石头 / 金币",
                has ? e.StoneCost + " / " + e.GoldCost : "—");
            UiUpgradeViews.Result(shell, result, m.Px(176f), "保护券",
                has ? (e.CouponUsed + " 张 · 剩 " + e.CouponLeft) : "—");
            UiUpgradeViews.Result(shell, result, m.Px(120f), "护级",
                has ? (e.ProtectedLevel ? "挡住了掉级" : "未触发") : "—");

            Debug.Log(ReadyLog + " slots=" + rows.Count + " tiers=" + UiUpgradeCodex.Tiers.Length
                + " has=" + (has ? "true" : "false"));
        }
    }
}

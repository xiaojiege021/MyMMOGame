using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 商城（原型 E01）。左边是入口清单，右边是购买结果。
    /// 清单外的编号会被服务端拒绝，元宝一分不动——界面不判「买得起买不起」，
    /// 标价、扣款、余额、发什么、绑定态全看 `/shop/buy` 回包。
    /// </summary>
    public static class UiShopScreen
    {
        public const string ReadyLog = "u6_shop_screen=ready";

        public static void Build(UiShell shell, UiCommerceFlow flow)
        {
            UiUpgradeViews.Note(shell, UiCommerceCodex.ShopNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.52f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;

            var chosen = UiCommerceCodex.Shop[0].Id;
            var chosenText = shell.Label(
                new Rect(pad, m.Px(300f), leftW, m.Px(40f)),
                "已选：" + UiCommerceCodex.Shop[0].Name, UiTextRole.Faint, shell.Size(24f),
                TextAnchor.MiddleLeft, false);

            var list = shell.Panel("能买的", m.Band(pad, top + m.Px(56f), leftW, m.Px(560f)), shell.Size(32f));
            for (var i = 0; i < UiCommerceCodex.Shop.Length; i++)
            {
                var goods = UiCommerceCodex.Shop[i];
                var opt = new UiOption(
                    list.Root,
                    new Rect(m.Px(24f), m.Px(40f) + i * m.Px(78f), leftW - m.Px(48f), m.Px(66f)),
                    goods.Id, goods.Name, goods.OneLine,
                    UiOptionKind.Card, shell.Size(26f), shell.Size(20f));
                opt.Selected = i == 0;
                var id = goods.Id;
                var name = goods.Name;
                opt.OnPick = picked =>
                {
                    chosen = id;
                    chosenText.Label.text = "已选：" + name;
                    Debug.Log("u6_shop_pick=" + id);
                };
                shell.Register(opt);
            }

            shell.Button(
                new Rect(pad, m.Px(220f), m.Px(320f), m.Px(72f)),
                "购买",
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.Buy(chosen, shell.RefreshPage);
                    }
                }, UiButtonKind.Primary);

            // 清单外拒绝：按钮只发一个不存在的编号，看回包怎么说。
            shell.Button(
                new Rect(pad + m.Px(360f), m.Px(220f), m.Px(380f), m.Px(72f)),
                "买一个不在清单里的",
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.Buy("no_such_goods", shell.RefreshPage);
                    }
                }, UiButtonKind.Normal);

            var result = shell.Panel("回包", m.Band(rightX, top + m.Px(56f), rightW, m.Px(520f)),
                shell.Size(32f));
            var s = flow.LastShop;
            var has = flow.HasShop;
            UiUpgradeViews.Result(shell, result, m.Px(400f), "结果",
                has ? (s.Ok ? "已买到" : "被拒：" + s.Reason) : "尚未请求");
            UiUpgradeViews.Result(shell, result, m.Px(344f), "标价 / 实扣",
                has ? s.PriceYuanbao + " / " + s.ChargedYuanbao : "—");
            UiUpgradeViews.Result(shell, result, m.Px(288f), "余额",
                has ? s.Balance.ToString() : "—");
            UiUpgradeViews.Result(shell, result, m.Px(232f), "发到背包",
                has ? (s.Delivered ? s.DeliveryItemId + " · 第 " + s.BagSlot + " 格" : "没发") : "—");
            UiUpgradeViews.Result(shell, result, m.Px(176f), "绑定",
                has ? (s.Bound ? "true" : "false") : "—");
            UiUpgradeViews.Result(shell, result, m.Px(120f), "状态",
                has ? s.State : "—");

            Debug.Log(ReadyLog + " goods=" + UiCommerceCodex.Shop.Length
                + " has=" + (has ? "true" : "false"));
        }
    }
}

using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 货币（原型 E03）。上半是 `/currency` 的余额，下半是内部充值的建单与回调。
    /// 元宝数、订单状态、余额都看回包；同一笔回调两次只发一次，第二次回 duplicate。
    /// </summary>
    public static class UiCurrencyScreen
    {
        public const string ReadyLog = "u6_currency_screen=ready";

        public static void Build(UiShell shell, UiCommerceFlow flow)
        {
            UiUpgradeViews.Note(shell, UiCommerceCodex.CurrencyNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.46f;

            var wallet = shell.Panel("余额", m.Band(pad, top + m.Px(56f), leftW, m.Px(320f)), shell.Size(32f));
            var rows = flow.Balances;
            UiUpgradeViews.Rows(shell, wallet,
                new[] { "元宝", "金币", "灵石" },
                new[]
                {
                    flow.HasCurrency ? UiCommerceCodex.BalanceOf(rows, "yuanbao") : "尚未读",
                    flow.HasCurrency ? UiCommerceCodex.BalanceOf(rows, "gold") : "尚未读",
                    flow.HasCurrency ? UiCommerceCodex.BalanceOf(rows, "spirit_stone") : "尚未读",
                },
                m.Px(56f), m.Px(28f));

            var recharge = shell.Panel("内部充值", m.Band(pad, top + m.Px(56f) + m.Px(360f), leftW, m.Px(420f)),
                shell.Size(32f));
            for (var i = 0; i < UiCommerceCodex.Recharge.Length; i++)
            {
                var goods = UiCommerceCodex.Recharge[i];
                var index = i;
                shell.ButtonIn(recharge.Root,
                    new Rect(m.Px(24f), m.Px(300f) - i * m.Px(96f), leftW - m.Px(48f), m.Px(76f)),
                    "建单：" + goods.Name,
                    () =>
                    {
                        if (!flow.Busy)
                        {
                            flow.RechargeOrder(UiCommerceCodex.Recharge[index].Id, shell.RefreshPage);
                        }
                    }, UiButtonKind.Normal);
            }

            shell.ButtonIn(recharge.Root,
                new Rect(m.Px(24f), m.Px(24f), leftW - m.Px(48f), m.Px(76f)),
                "回调最近一笔订单",
                () =>
                {
                    var order = flow.LastOrder.OrderId;
                    if (!flow.Busy && !string.IsNullOrEmpty(order))
                    {
                        flow.RechargeCallback(order, shell.RefreshPage);
                    }
                }, UiButtonKind.Primary);

            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;
            var result = shell.Panel("回包", m.Band(rightX, top + m.Px(56f), rightW, m.Px(640f)),
                shell.Size(32f));
            var o = flow.LastOrder;
            var st = flow.LastSettle;
            UiUpgradeViews.Result(shell, result, m.Px(520f), "订单",
                flow.HasOrder ? (o.Ok ? o.OrderId + " · " + o.Status : "没建成：" + o.Reason) : "尚未建单");
            UiUpgradeViews.Result(shell, result, m.Px(464f), "商品 / 元宝",
                flow.HasOrder && o.Ok ? o.ProductId + " / " + o.Yuanbao : "—");
            UiUpgradeViews.Result(shell, result, m.Px(408f), "回调结果",
                flow.HasSettle ? (st.Ok ? "已发 " + st.Yuanbao : "没结算：" + st.Reason) : "尚未回调");
            UiUpgradeViews.Result(shell, result, m.Px(352f), "余额",
                flow.HasSettle ? st.Balance.ToString() : "—");
            UiUpgradeViews.Result(shell, result, m.Px(296f), "重放",
                flow.HasSettle ? (st.Duplicate ? "duplicate（没再发）" : "false") : "—");

            Debug.Log(ReadyLog + " balances=" + (flow.HasCurrency ? rows.Count : 0)
                + " order=" + (flow.HasOrder ? "true" : "false")
                + " settle=" + (flow.HasSettle ? "true" : "false"));
        }
    }
}

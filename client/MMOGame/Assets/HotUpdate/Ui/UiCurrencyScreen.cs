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

            var wallet = shell.Panel(UiStrings.UiCurrencyScreen.T001, m.Band(pad, top + m.Px(56f), leftW, m.Px(320f)), shell.Size(32f));
            var rows = flow.Balances;
            UiUpgradeViews.Rows(shell, wallet,
                new[] { UiStrings.UiCurrencyScreen.T002, UiStrings.UiCurrencyScreen.T003, UiStrings.UiCurrencyScreen.T004 },
                new[]
                {
                    flow.HasCurrency ? UiCommerceCodex.BalanceOf(rows, "yuanbao") : UiStrings.UiCurrencyScreen.T005,
                    flow.HasCurrency ? UiCommerceCodex.BalanceOf(rows, "gold") : UiStrings.UiCurrencyScreen.T005,
                    flow.HasCurrency ? UiCommerceCodex.BalanceOf(rows, "spirit_stone") : UiStrings.UiCurrencyScreen.T005,
                },
                m.Px(56f), m.Px(28f));

            var recharge = shell.Panel(UiStrings.UiCurrencyScreen.T006, m.Band(pad, top + m.Px(56f) + m.Px(360f), leftW, m.Px(420f)),
                shell.Size(32f));
            for (var i = 0; i < UiCommerceCodex.Recharge.Length; i++)
            {
                var goods = UiCommerceCodex.Recharge[i];
                var index = i;
                shell.ButtonIn(recharge.Root,
                    new Rect(m.Px(24f), m.Px(300f) - i * m.Px(96f), leftW - m.Px(48f), m.Px(76f)),
                    UiStrings.UiCurrencyScreen.T007 + goods.Name,
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
                UiStrings.UiCurrencyScreen.T008,
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
            var result = shell.Panel(UiStrings.UiCurrencyScreen.T009, m.Band(rightX, top + m.Px(56f), rightW, m.Px(640f)),
                shell.Size(32f));
            var o = flow.LastOrder;
            var st = flow.LastSettle;
            UiUpgradeViews.Result(shell, result, m.Px(520f), UiStrings.UiCurrencyScreen.T010,
                flow.HasOrder ? (o.Ok ? o.OrderId + " · " + o.Status : UiStrings.UiCurrencyScreen.T011 + o.Reason) : UiStrings.UiCurrencyScreen.T012);
            UiUpgradeViews.Result(shell, result, m.Px(464f), UiStrings.UiCurrencyScreen.T013,
                flow.HasOrder && o.Ok ? o.ProductId + " / " + o.Yuanbao : "—");
            UiUpgradeViews.Result(shell, result, m.Px(408f), UiStrings.UiCurrencyScreen.T014,
                flow.HasSettle ? (st.Ok ? UiStrings.UiCurrencyScreen.T015 + st.Yuanbao : UiStrings.UiCurrencyScreen.T016 + st.Reason) : UiStrings.UiCurrencyScreen.T017);
            UiUpgradeViews.Result(shell, result, m.Px(352f), UiStrings.UiCurrencyScreen.T001,
                flow.HasSettle ? st.Balance.ToString() : "—");
            UiUpgradeViews.Result(shell, result, m.Px(296f), UiStrings.UiCurrencyScreen.T018,
                flow.HasSettle ? (st.Duplicate ? UiStrings.UiCurrencyScreen.T019 : "false") : "—");

            Debug.Log(ReadyLog + " balances=" + (flow.HasCurrency ? rows.Count : 0)
                + " order=" + (flow.HasOrder ? "true" : "false")
                + " settle=" + (flow.HasSettle ? "true" : "false"));
        }
    }
}

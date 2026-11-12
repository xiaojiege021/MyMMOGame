using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 拍卖与寄售（原型 E02）。上架、购买、元宝挂单、元宝购买各一个入口；
    /// 成交价、税、净额、双方余额全看回包，界面不本地算税，也不判挂单价格高低。
    /// </summary>
    public static class UiAuctionScreen
    {
        public const string ReadyLog = "u6_auction_screen=ready";

        public static void Build(UiShell shell, UiCommerceFlow flow)
        {
            UiUpgradeViews.Note(shell, UiCommerceCodex.AuctionNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.46f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;

            var slot = 0u;
            var price = UiCommerceCodex.PriceSteps[0];
            var rows = flow.HasBag ? UiUpgradeCodex.FirstSlots(flow.Bag, 4) : new List<Mmo.Net.BagSlotView>();
            if (rows.Count > 0)
            {
                slot = rows[0].Slot;
            }

            var pick = shell.Panel("上架哪一格", m.Band(pad, top + m.Px(56f), leftW, m.Px(300f)), shell.Size(30f));
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var opt = new UiOption(
                    pick.Root, new Rect(m.Px(24f), m.Px(56f) + i * m.Px(64f), leftW - m.Px(48f), m.Px(56f)),
                    row.Slot.ToString(), "第 " + row.Slot + " 格", row.ItemId + " × " + row.Count,
                    UiOptionKind.Card, shell.Size(22f), shell.Size(18f));
                opt.Selected = i == 0;
                var s = row.Slot;
                opt.OnPick = picked => slot = s;
                shell.Register(opt);
            }

            var pricePanel = shell.Panel("标价（金币）", m.Band(pad, top + m.Px(56f) + m.Px(340f), leftW, m.Px(200f)),
                shell.Size(28f));
            for (var i = 0; i < UiCommerceCodex.PriceSteps.Length; i++)
            {
                var step = UiCommerceCodex.PriceSteps[i];
                var opt = new UiOption(
                    pricePanel.Root,
                    new Rect(m.Px(20f) + i * m.Px(200f), m.Px(64f), m.Px(180f), m.Px(60f)),
                    step.ToString(), step.ToString(), "",
                    UiOptionKind.Chip, shell.Size(22f), shell.Size(18f));
                opt.Selected = i == 0;
                opt.OnPick = picked => price = step;
                shell.Register(opt);
            }

            shell.Button(
                new Rect(pad, m.Px(200f), m.Px(300f), m.Px(72f)),
                "上架",
                () =>
                {
                    if (!flow.Busy && slot != 0)
                    {
                        flow.AuctionList(slot, 1, price, shell.RefreshPage);
                    }
                }, UiButtonKind.Primary);

            shell.Button(
                new Rect(pad + m.Px(340f), m.Px(200f), m.Px(320f), m.Px(72f)),
                "买下最近挂单",
                () =>
                {
                    var id = flow.LastListingId;
                    if (!flow.Busy && id != 0)
                    {
                        flow.AuctionBuy(id, shell.RefreshPage);
                    }
                }, UiButtonKind.Normal);

            shell.Button(
                new Rect(pad, m.Px(110f), m.Px(300f), m.Px(72f)),
                "元宝挂单 100",
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.IngotList(100, 1000, shell.RefreshPage);
                    }
                }, UiButtonKind.Normal);

            shell.Button(
                new Rect(pad + m.Px(340f), m.Px(110f), m.Px(320f), m.Px(72f)),
                "买下最近元宝单",
                () =>
                {
                    var id = flow.LastListingId;
                    if (!flow.Busy && id != 0)
                    {
                        flow.IngotBuy(id, shell.RefreshPage);
                    }
                }, UiButtonKind.Normal);

            var result = shell.Panel("回包", m.Band(rightX, top + m.Px(56f), rightW, m.Px(640f)),
                shell.Size(32f));
            var a = flow.LastAuction;
            var i2 = flow.LastIngot;
            UiUpgradeViews.Result(shell, result, m.Px(520f), "拍卖",
                flow.HasAuction ? (a.Ok ? "成交/上架：" + a.State : "被拒：" + a.Reason) : "尚未请求");
            UiUpgradeViews.Result(shell, result, m.Px(464f), "标价 / 税 / 净额",
                flow.HasAuction ? a.PriceGold + " / " + a.TaxGold + " / " + a.NetGold : "—");
            UiUpgradeViews.Result(shell, result, m.Px(408f), "买方 / 卖方金币",
                flow.HasAuction ? a.BuyerGold + " / " + a.SellerGold : "—");
            UiUpgradeViews.Result(shell, result, m.Px(352f), "寄售",
                flow.HasIngot ? (i2.Ok ? "已挂/已成交：" + i2.State : "被拒：" + i2.Reason) : "尚未请求");
            UiUpgradeViews.Result(shell, result, m.Px(296f), "元宝 / 金币",
                flow.HasIngot ? i2.IngotAmount + " / " + i2.PriceGold : "—");
            UiUpgradeViews.Result(shell, result, m.Px(240f), "税 / 净额",
                flow.HasIngot ? i2.TaxGold + " / " + i2.NetGold : "—");

            Debug.Log(ReadyLog + " slots=" + rows.Count + " price=" + price);
        }
    }
}

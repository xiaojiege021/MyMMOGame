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

            var pick = shell.Panel(UiStrings.UiAuctionScreen.T001, m.Band(pad, top + m.Px(56f), leftW, m.Px(300f)), shell.Size(30f));
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var opt = new UiOption(
                    pick.Root, new Rect(m.Px(24f), m.Px(56f) + i * m.Px(64f), leftW - m.Px(48f), m.Px(56f)),
                    row.Slot.ToString(), UiStrings.UiAuctionScreen.T002 + row.Slot + UiStrings.UiAuctionScreen.T003, row.ItemId + " × " + row.Count,
                    UiOptionKind.Card, shell.Size(22f), shell.Size(18f));
                opt.Selected = i == 0;
                var s = row.Slot;
                opt.OnPick = picked => slot = s;
                shell.Register(opt);
            }

            var pricePanel = shell.Panel(UiStrings.UiAuctionScreen.T004, m.Band(pad, top + m.Px(56f) + m.Px(340f), leftW, m.Px(200f)),
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
                UiStrings.UiAuctionScreen.T005,
                () =>
                {
                    if (!flow.Busy && slot != 0)
                    {
                        flow.AuctionList(slot, 1, price, shell.RefreshPage);
                    }
                }, UiButtonKind.Primary);

            shell.Button(
                new Rect(pad + m.Px(340f), m.Px(200f), m.Px(320f), m.Px(72f)),
                UiStrings.UiAuctionScreen.T006,
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
                UiStrings.UiAuctionScreen.T007,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.IngotList(100, 1000, shell.RefreshPage);
                    }
                }, UiButtonKind.Normal);

            shell.Button(
                new Rect(pad + m.Px(340f), m.Px(110f), m.Px(320f), m.Px(72f)),
                UiStrings.UiAuctionScreen.T008,
                () =>
                {
                    var id = flow.LastListingId;
                    if (!flow.Busy && id != 0)
                    {
                        flow.IngotBuy(id, shell.RefreshPage);
                    }
                }, UiButtonKind.Normal);

            var result = shell.Panel(UiStrings.UiAuctionScreen.T009, m.Band(rightX, top + m.Px(56f), rightW, m.Px(640f)),
                shell.Size(32f));
            var a = flow.LastAuction;
            var i2 = flow.LastIngot;
            UiUpgradeViews.Result(shell, result, m.Px(520f), UiStrings.UiAuctionScreen.T010,
                flow.HasAuction ? (a.Ok ? UiStrings.UiAuctionScreen.T011 + a.State : UiStrings.UiAuctionScreen.T012 + a.Reason) : UiStrings.UiAuctionScreen.T013);
            UiUpgradeViews.Result(shell, result, m.Px(464f), UiStrings.UiAuctionScreen.T014,
                flow.HasAuction ? a.PriceGold + " / " + a.TaxGold + " / " + a.NetGold : "—");
            UiUpgradeViews.Result(shell, result, m.Px(408f), UiStrings.UiAuctionScreen.T015,
                flow.HasAuction ? a.BuyerGold + " / " + a.SellerGold : "—");
            UiUpgradeViews.Result(shell, result, m.Px(352f), UiStrings.UiAuctionScreen.T016,
                flow.HasIngot ? (i2.Ok ? UiStrings.UiAuctionScreen.T017 + i2.State : UiStrings.UiAuctionScreen.T012 + i2.Reason) : UiStrings.UiAuctionScreen.T013);
            UiUpgradeViews.Result(shell, result, m.Px(296f), UiStrings.UiAuctionScreen.T018,
                flow.HasIngot ? i2.IngotAmount + " / " + i2.PriceGold : "—");
            UiUpgradeViews.Result(shell, result, m.Px(240f), UiStrings.UiAuctionScreen.T019,
                flow.HasIngot ? i2.TaxGold + " / " + i2.NetGold : "—");

            Debug.Log(ReadyLog + " slots=" + rows.Count + " price=" + price);
        }
    }
}

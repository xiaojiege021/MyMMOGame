using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 摆摊（U20 / B5 的界面欠项）。左边是这块地表上世界服报上来的摊（只有招牌、摊主、
    /// 在售件数与坐标），选中一个可以看它的在售挂单；右边开摊 / 关摊 / 上架，
    /// 成交走「买下选中的挂单」。
    ///
    /// 一条判定都不在客户端：够不够格开摊、8 格满没满、绑定能不能上架、税多少、
    /// 谁付多少，全看 `/stall/*` 的回包；摊号也只认开摊回包给的那一个。
    /// </summary>
    public static class UiStallScreen
    {
        public const string ReadyLog = "u6_stall_screen=ready";

        /// <summary>单价的几档（金币）。只作请求参数，成交与税看回包。</summary>
        public static readonly long[] PriceSteps = { 100L, 1000L, 10000L };

        public static void Build(UiShell shell, UiCommerceFlow flow)
        {
            UiUpgradeViews.Note(shell, UiCommerceCodex.StallNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.5f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;

            // 左上：这块地表上的摊。列表顺序照世界服回包，不按在售件数重排。
            var browse = flow.LastStallBrowse;
            var stalls = flow.HasStallBrowse && browse.Stalls != null ? browse.Stalls : null;
            var listPanel = shell.Panel(UiStrings.UiStallScreen.T001,
                m.Band(pad, top + m.Px(56f), leftW, m.Px(430f)), shell.Size(30f));
            if (stalls == null || stalls.Count == 0)
            {
                shell.LabelIn(listPanel.Root, new Rect(m.Px(28f), m.Px(64f), leftW - m.Px(56f), m.Px(96f)),
                    flow.HasStallBrowse ? UiStrings.UiStallScreen.T004 : UiStrings.UiStallScreen.T003,
                    UiTextRole.Faint, shell.Size(24f), TextAnchor.MiddleLeft, false);
            }
            else
            {
                var show = stalls.Count > 4 ? 4 : stalls.Count;
                for (var i = 0; i < show; i++)
                {
                    var row = stalls[i];
                    var opt = new UiOption(
                        listPanel.Root,
                        new Rect(m.Px(24f), m.Px(58f) + i * m.Px(66f), leftW - m.Px(48f), m.Px(58f)),
                        row.Title, row.OwnerName,
                        row.LiveListings + UiStrings.UiStallScreen.T026,
                        UiOptionKind.Card, shell.Size(22f), shell.Size(18f));
                    opt.Selected = row.StallId == flow.StallViewId;
                    var id = row.StallId;
                    opt.OnPick = picked =>
                    {
                        flow.StallViewId = id;
                        shell.RefreshPage();
                    };
                    shell.Register(opt);
                }
            }

            shell.ButtonIn(listPanel.Root, new Rect(m.Px(24f), m.Px(340f), m.Px(220f), m.Px(64f)),
                UiStrings.UiStallScreen.T002,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.BrowseStalls(shell.RefreshPage);
                    }
                }, UiButtonKind.Normal);

            // 左下：选中摊的在售挂单。选中一件才能买。
            var view = flow.LastStallView;
            var listings = flow.HasStallView && view.Listings != null ? view.Listings : null;
            var itemsPanel = shell.Panel(UiStrings.UiStallScreen.T027,
                m.Band(pad, top + m.Px(56f) + m.Px(450f), leftW, m.Px(420f)), shell.Size(28f));
            if (listings == null || listings.Count == 0)
            {
                shell.LabelIn(itemsPanel.Root, new Rect(m.Px(28f), m.Px(60f), leftW - m.Px(56f), m.Px(88f)),
                    flow.HasStallView ? UiStrings.UiStallScreen.T023 : UiStrings.UiStallScreen.T003,
                    UiTextRole.Faint, shell.Size(24f), TextAnchor.MiddleLeft, false);
            }
            else
            {
                var show = listings.Count > 3 ? 3 : listings.Count;
                for (var i = 0; i < show; i++)
                {
                    var row = listings[i];
                    var opt = new UiOption(
                        itemsPanel.Root,
                        new Rect(m.Px(24f), m.Px(56f) + i * m.Px(70f), leftW - m.Px(48f), m.Px(62f)),
                        row.ItemName.Length > 0 ? row.ItemName : row.ItemId,
                        UiStrings.UiStallScreen.T010 + row.Count + UiStrings.UiStallScreen.T011,
                        row.PriceGold.ToString(), UiOptionKind.Card, shell.Size(22f), shell.Size(18f));
                    opt.Selected = row.ListingId == flow.StallListingId;
                    var id = row.ListingId;
                    opt.OnPick = picked =>
                    {
                        flow.StallListingId = id;
                        shell.RefreshPage();
                    };
                    shell.Register(opt);
                }
            }

            shell.ButtonIn(itemsPanel.Root, new Rect(m.Px(24f), m.Px(330f), m.Px(220f), m.Px(64f)),
                UiStrings.UiStallScreen.T007,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.ViewStall(flow.StallViewId, shell.RefreshPage);
                    }
                }, UiButtonKind.Normal);
            shell.ButtonIn(itemsPanel.Root, new Rect(m.Px(264f), m.Px(330f), m.Px(260f), m.Px(64f)),
                UiStrings.UiStallScreen.T008,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.BuyStallItem(flow.StallListingId, shell.RefreshPage);
                    }
                }, UiButtonKind.Primary);

            // 右上：开摊 / 关摊。
            var openPanel = shell.Panel(UiStrings.UiStallScreen.T015,
                m.Band(rightX, top + m.Px(56f), rightW, m.Px(260f)), shell.Size(28f));
            shell.ButtonIn(openPanel.Root, new Rect(m.Px(28f), m.Px(64f), m.Px(240f), m.Px(72f)),
                UiStrings.UiStallScreen.T005,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.OpenStall(flow.StallRegion, UiStrings.UiStallScreen.T025, shell.RefreshPage);
                    }
                }, UiButtonKind.Primary);
            shell.ButtonIn(openPanel.Root, new Rect(m.Px(288f), m.Px(64f), m.Px(240f), m.Px(72f)),
                UiStrings.UiStallScreen.T006,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.CloseStall(shell.RefreshPage);
                    }
                }, UiButtonKind.Normal);
            UiUpgradeViews.Result(shell, openPanel, m.Px(180f), UiStrings.UiStallScreen.T018,
                flow.StallId.Length > 0 ? flow.StallId : "—");

            // 右上二：上架（选格号 / 数量 / 单价）。格号上限取背包回包，不超服务端会拒的数。
            var listTop = top + m.Px(56f) + m.Px(280f);
            var listPanel2 = shell.Panel(UiStrings.UiStallScreen.T009,
                m.Band(rightX, listTop, rightW, m.Px(430f)), shell.Size(28f));
            var bagSlots = flow.HasBag && flow.Bag.BagSlots > 0 ? (uint)flow.Bag.BagSlots : 1u;
            if (flow.StallListSlot < 1)
            {
                flow.StallListSlot = 1;
            }

            if (flow.StallListSlot > bagSlots)
            {
                flow.StallListSlot = (int)bagSlots;
            }

            if (flow.StallListCount < 1)
            {
                flow.StallListCount = 1;
            }

            UiUpgradeViews.Result(shell, listPanel2, m.Px(330f), UiStrings.UiStallScreen.T028,
                UiStrings.UiStallScreen.T010 + flow.StallListSlot + UiStrings.UiStallScreen.T011 + " / " + flow.StallListCount);

            var stepW = (rightW - m.Px(56f) - m.Px(36f)) * 0.25f;
            shell.ButtonIn(listPanel2.Root, new Rect(m.Px(28f), m.Px(220f), stepW, m.Px(64f)), "−",
                () =>
                {
                    flow.StallListSlot = flow.StallListSlot > 1 ? flow.StallListSlot - 1 : 1;
                    shell.RefreshPage();
                }, UiButtonKind.Normal);
            shell.ButtonIn(listPanel2.Root, new Rect(m.Px(28f) + stepW + m.Px(12f), m.Px(220f), stepW, m.Px(64f)), "+",
                () =>
                {
                    if (flow.StallListSlot < bagSlots)
                    {
                        flow.StallListSlot++;
                    }

                    shell.RefreshPage();
                }, UiButtonKind.Normal);
            shell.ButtonIn(listPanel2.Root, new Rect(m.Px(28f) + (stepW + m.Px(12f)) * 2f, m.Px(220f), stepW, m.Px(64f)), "−",
                () =>
                {
                    flow.StallListCount = flow.StallListCount > 1 ? flow.StallListCount - 1 : 1;
                    shell.RefreshPage();
                }, UiButtonKind.Normal);
            shell.ButtonIn(listPanel2.Root, new Rect(m.Px(28f) + (stepW + m.Px(12f)) * 3f, m.Px(220f), stepW, m.Px(64f)), "+",
                () =>
                {
                    flow.StallListCount++;
                    shell.RefreshPage();
                }, UiButtonKind.Normal);

            for (var i = 0; i < PriceSteps.Length; i++)
            {
                var price = PriceSteps[i];
                var opt = new UiOption(
                    listPanel2.Root,
                    new Rect(m.Px(28f) + i * m.Px(180f), m.Px(130f), m.Px(164f), m.Px(56f)),
                    price.ToString(), UiStrings.UiStallScreen.T012, string.Empty,
                    UiOptionKind.Chip, shell.Size(22f), shell.Size(18f));
                opt.Selected = flow.StallPrice == price;
                opt.OnPick = picked =>
                {
                    flow.StallPrice = price;
                    shell.RefreshPage();
                };
                shell.Register(opt);
            }

            shell.ButtonIn(listPanel2.Root, new Rect(m.Px(28f), m.Px(300f), m.Px(200f), m.Px(64f)),
                UiStrings.UiStallScreen.T013,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.ListStallItem(flow.StallListSlot, flow.StallListCount, flow.StallPrice,
                            shell.RefreshPage);
                    }
                }, UiButtonKind.Primary);

            // 右下：回包。开摊 / 关摊 / 上架 / 成交各占一组，被拒时拒绝码原样带出。
            var result = shell.Panel(UiStrings.UiStallScreen.T014,
                m.Band(rightX, top + m.Px(56f) + m.Px(740f), rightW, m.Px(360f)), shell.Size(28f));
            var open = flow.LastStallOpen;
            var close = flow.LastStallClose;
            var listing = flow.LastStallList;
            var buy = flow.LastStallBuy;
            UiUpgradeViews.Result(shell, result, m.Px(270f), UiStrings.UiStallScreen.T015,
                flow.HasStallOpen
                    ? (open.Ok ? open.DisplayText : UiStrings.UiStallScreen.T022 + open.Reason)
                    : (flow.HasStallClose
                        ? (close.Ok ? close.DisplayText : UiStrings.UiStallScreen.T022 + close.Reason)
                        : UiStrings.UiStallScreen.T003));
            UiUpgradeViews.Result(shell, result, m.Px(216f), UiStrings.UiStallScreen.T016,
                flow.HasStallList
                    ? (listing.Ok ? listing.DisplayText : UiStrings.UiStallScreen.T022 + listing.Reason)
                    : UiStrings.UiStallScreen.T003);
            UiUpgradeViews.Result(shell, result, m.Px(162f), UiStrings.UiStallScreen.T019,
                flow.HasStallList && listing.Ok ? listing.LiveListings + " / " + listing.Slots : "—");
            UiUpgradeViews.Result(shell, result, m.Px(108f), UiStrings.UiStallScreen.T017,
                flow.HasStallBuy
                    ? (buy.Ok ? buy.DisplayText : UiStrings.UiStallScreen.T022 + buy.Reason)
                    : UiStrings.UiStallScreen.T003);
            UiUpgradeViews.Result(shell, result, m.Px(54f), UiStrings.UiStallScreen.T020,
                flow.HasStallBuy && buy.Ok ? buy.PriceGold + " / " + buy.TaxGold + " / " + buy.NetGold : "—");

            Debug.Log(ReadyLog + " region=" + flow.StallRegion
                + " stalls=" + (stalls != null ? stalls.Count : 0)
                + " listings=" + (listings != null ? listings.Count : 0)
                + " stall=" + flow.StallId
                + " price=" + flow.StallPrice);
        }
    }
}

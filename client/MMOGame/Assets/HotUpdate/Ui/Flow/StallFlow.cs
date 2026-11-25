using System;
using Mmo.Net;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 摆摊（U20 / B5 的界面欠项）。六条口：开摊 / 关摊 / 上架 / 看摊 /
    /// 地表摊位列表 / 成交。
    ///
    /// 摊位在世界服显示（只存内存摘要），成交仍提交拍卖那一笔角色服事务；
    /// 税按挂单 `channel` 取 `config/stall.json`，世界服不碰背包。这里只发格号、
    /// 数量、单价与招牌：够不够格、8 格满没满、绑定能不能上架、税多少全看回包；
    /// 事务号由网关生成，请求体里不带。
    /// </summary>
    public sealed partial class UiCommerceFlow : MonoBehaviour
    {
        // ------------------------------------------------------------------ 摆摊（U20 / B5）

        public StallOpenOutcome LastStallOpen { get; private set; }

        public bool HasStallOpen { get; private set; }

        public StallCloseOutcome LastStallClose { get; private set; }

        public bool HasStallClose { get; private set; }

        public StallListOutcome LastStallList { get; private set; }

        public bool HasStallList { get; private set; }

        public StallViewOutcome LastStallView { get; private set; }

        public bool HasStallView { get; private set; }

        public StallBrowseOutcome LastStallBrowse { get; private set; }

        public bool HasStallBrowse { get; private set; }

        public StallBuyOutcome LastStallBuy { get; private set; }

        public bool HasStallBuy { get; private set; }

        /// <summary>当前摊号：只认开摊回包给的那一个，界面不自己编。</summary>
        public string StallId { get; private set; } = string.Empty;

        /// <summary>这一屏看的是哪块地表。世界服按地表列摊，坐标与招牌都照回包。</summary>
        public string StallRegion { get; set; } = string.Empty;

        /// <summary>选中的摊（看摊用）。刷新屏不丢，界面不另存一份摊状态。</summary>
        public string StallViewId { get; set; } = string.Empty;

        /// <summary>选中的挂单（买用）。只认看摊回包里给过的编号。</summary>
        public ulong StallListingId { get; set; }

        /// <summary>上架参数：格号 / 数量 / 单价。界面只负责调，能不能上架看回包。</summary>
        public int StallListSlot { get; set; } = 1;

        public int StallListCount { get; set; } = 1;

        public long StallPrice { get; set; } = 100L;

        /// <summary>开摊。地表与招牌由界面带，坐标先写 0（地表摆点由世界服后续补）。</summary>
        public void OpenStall(string regionId, string title, Action done = null)
        {
            if (_characterId == 0)
            {
                LastStallOpen = new StallOpenOutcome { Ok = false, Reason = "no_character", DisplayText = UiStrings.StallFlow.T009 };
                HasStallOpen = true;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterStall.OpenPath, CharacterStall.OpenBody(_characterId, regionId, title), _token,
                (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        LastStallOpen = CharacterStall.ParseOpen(text);
                        if (LastStallOpen.Ok)
                        {
                            StallId = LastStallOpen.StallId;
                        }

                        Error = LastStallOpen.Ok ? string.Empty : LastStallOpen.DisplayText;
                        Debug.Log("u20_stall_open=" + (LastStallOpen.Ok ? "ok" : "reject")
                            + " stall=" + LastStallOpen.StallId
                            + " region=" + LastStallOpen.RegionId
                            + " live=" + LastStallOpen.LiveListings
                            + " slots=" + LastStallOpen.Slots
                            + " min_level=" + LastStallOpen.MinLevel
                            + " tax=" + LastStallOpen.TaxRatePct
                            + " shown=" + (LastStallOpen.Shown ? "true" : "false")
                            + " reason=" + LastStallOpen.Reason);
                    }
                    else
                    {
                        LastStallOpen = new StallOpenOutcome
                        {
                            Ok = false, Reason = "http_" + status, DisplayText = UiStrings.StallFlow.T001 + status,
                        };
                        Error = UiStrings.StallFlow.T001 + status;
                        Debug.Log("u20_stall_open=reject status=" + status);
                    }

                    HasStallOpen = true;
                    if (done != null) done();
                });
        }

        /// <summary>关摊。在售挂单由角色服同一笔事务撤成 cancelled，物品不搬。</summary>
        public void CloseStall(Action done = null)
        {
            if (_characterId == 0 || StallId.Length == 0)
            {
                LastStallClose = new StallCloseOutcome
                {
                    Ok = false, Reason = "no_stall", DisplayText = UiStrings.StallFlow.T002,
                };
                HasStallClose = true;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterStall.ClosePath, CharacterStall.CloseBody(_characterId, StallId), _token,
                (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        LastStallClose = CharacterStall.ParseClose(text);
                        Error = LastStallClose.Ok ? string.Empty : LastStallClose.DisplayText;
                        Debug.Log("u20_stall_close=" + (LastStallClose.Ok ? "ok" : "reject")
                            + " stall=" + LastStallClose.StallId
                            + " cancelled=" + LastStallClose.CancelledListings
                            + " reason=" + LastStallClose.Reason);
                    }
                    else
                    {
                        LastStallClose = new StallCloseOutcome
                        {
                            Ok = false, Reason = "http_" + status, DisplayText = UiStrings.StallFlow.T003 + status,
                        };
                        Error = UiStrings.StallFlow.T003 + status;
                        Debug.Log("u20_stall_close=reject status=" + status);
                    }

                    HasStallClose = true;
                    if (done != null) done();
                });
        }

        /// <summary>上架一件。格号 / 数量 / 单价由界面带，能不能上架在角色服判。</summary>
        public void ListStallItem(int slot, int count, long priceGold, Action done = null)
        {
            if (_characterId == 0 || StallId.Length == 0)
            {
                LastStallList = new StallListOutcome
                {
                    Ok = false, Reason = "no_stall", DisplayText = UiStrings.StallFlow.T002,
                };
                HasStallList = true;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterStall.ListPath,
                CharacterStall.ListingBody(_characterId, StallId, slot, count, priceGold), _token,
                (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        LastStallList = CharacterStall.ParseListing(text);
                        Error = LastStallList.Ok ? string.Empty : LastStallList.DisplayText;
                        Debug.Log("u20_stall_list=" + (LastStallList.Ok ? "ok" : "reject")
                            + " slot=" + LastStallList.Slot
                            + " item=" + LastStallList.ItemId
                            + " count=" + LastStallList.Count
                            + " price=" + LastStallList.PriceGold
                            + " live=" + LastStallList.LiveListings
                            + " reason=" + LastStallList.Reason);
                    }
                    else
                    {
                        LastStallList = new StallListOutcome
                        {
                            Ok = false, Reason = "http_" + status, DisplayText = UiStrings.StallFlow.T004 + status,
                        };
                        Error = UiStrings.StallFlow.T004 + status;
                        Debug.Log("u20_stall_list=reject status=" + status);
                    }

                    HasStallList = true;
                    if (done != null) done();
                });
        }

        /// <summary>看一个摊（只读）。在售件与价格由角色服从库里读，世界服不参与。</summary>
        public void ViewStall(string stallId, Action done = null)
        {
            var id = string.IsNullOrEmpty(stallId) ? StallId : stallId;
            if (id.Length == 0)
            {
                LastStallView = new StallViewOutcome
                {
                    Ok = false, Found = false, Reason = "no_stall", DisplayText = UiStrings.StallFlow.T002,
                    Listings = new System.Collections.Generic.List<StallListingView>(),
                };
                HasStallView = true;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterStall.ViewQuery(id), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    LastStallView = CharacterStall.ParseView(text);
                    Error = LastStallView.Ok ? string.Empty : LastStallView.DisplayText;
                    Debug.Log("u20_stall_view=" + (LastStallView.Found ? "ok" : "missing")
                        + " stall=" + LastStallView.StallId
                        + " state=" + LastStallView.State
                        + " rows=" + LastStallView.Listings.Count
                        + " live=" + LastStallView.LiveListings);
                }
                else
                {
                    LastStallView = new StallViewOutcome
                    {
                        Ok = false, Found = false, Reason = "http_" + status,
                        DisplayText = UiStrings.StallFlow.T005 + status,
                        Listings = new System.Collections.Generic.List<StallListingView>(),
                    };
                    Error = UiStrings.StallFlow.T005 + status;
                    Debug.Log("u20_stall_view=reject status=" + status);
                }

                HasStallView = true;
                if (done != null) done();
            });
        }

        /// <summary>列这块地表的摊。世界服只有摘要，不连角色库。</summary>
        public void BrowseStalls(Action done = null)
        {
            Busy = true;
            Requests++;
            _send(CharacterStall.BrowseQuery(StallRegion), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    LastStallBrowse = CharacterStall.ParseBrowse(text);
                    Error = string.Empty;
                    Debug.Log("u20_stalls=ok region=" + LastStallBrowse.RegionId
                        + " count=" + LastStallBrowse.Count
                        + " rows=" + LastStallBrowse.Stalls.Count);
                }
                else
                {
                    LastStallBrowse = new StallBrowseOutcome
                    {
                        Ok = false, Reason = "http_" + status, DisplayText = UiStrings.StallFlow.T006 + status,
                        Stalls = new System.Collections.Generic.List<StallRowView>(),
                    };
                    Error = UiStrings.StallFlow.T006 + status;
                    Debug.Log("u20_stalls=reject status=" + status);
                }

                HasStallBrowse = true;
                if (done != null) done();
            });
        }

        /// <summary>买一件在售。只发挂单编号；价格、税、净额、双方余额都看回包。</summary>
        public void BuyStallItem(ulong listingId, Action done = null)
        {
            if (_characterId == 0 || listingId == 0)
            {
                LastStallBuy = new StallBuyOutcome
                {
                    Ok = false, Reason = "no_listing", DisplayText = UiStrings.StallFlow.T007,
                };
                HasStallBuy = true;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterStall.BuyPath, CharacterStall.BuyBody(_characterId, listingId), _token,
                (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        LastStallBuy = CharacterStall.ParseBuy(text);
                        Error = LastStallBuy.Ok ? string.Empty : LastStallBuy.DisplayText;
                        Debug.Log("u20_stall_buy=" + (LastStallBuy.Ok ? "ok" : "reject")
                            + " listing=" + LastStallBuy.ListingId
                            + " price=" + LastStallBuy.PriceGold
                            + " tax=" + LastStallBuy.TaxGold
                            + " net=" + LastStallBuy.NetGold
                            + " live=" + LastStallBuy.LiveListings
                            + " duplicate=" + (LastStallBuy.Duplicate ? "true" : "false")
                            + " reason=" + LastStallBuy.Reason);
                    }
                    else
                    {
                        LastStallBuy = new StallBuyOutcome
                        {
                            Ok = false, Reason = "http_" + status, DisplayText = UiStrings.StallFlow.T008 + status,
                        };
                        Error = UiStrings.StallFlow.T008 + status;
                        Debug.Log("u20_stall_buy=reject status=" + status);
                    }

                    HasStallBuy = true;
                    if (done != null) done();
                });
        }
    }
}

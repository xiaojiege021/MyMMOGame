using System;
using System.Collections.Generic;
using Mmo.Net;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U6 经济与商城四屏的流程与唯一判定出口（原型 E01–E04）。
    ///
    /// 只做三件事：发请求、拆回包、把字段交给屏。标价、税额、净额、余额、绑定态
    /// 一条都不在这里算——屏上的数要么来自 `/currency`、`/inventory`，
    /// 要么来自 `/shop/buy`、`/auction/*`、`/recharge/*` 的回包。
    /// </summary>
    public sealed class UiCommerceFlow : MonoBehaviour
    {
        public const string ReadyLog = "u6_commerce=ready";

        public static string Gateway = "http://127.0.0.1:61000";

        public delegate void CommerceSend(string path, string body, string token, Action<int, string> done);

        UiShell _shell;

        CommerceSend _send;

        string _token = string.Empty;

        ulong _characterId;

        public bool Busy { get; private set; }

        public int Requests { get; private set; }

        public List<CurrencyRow> Balances { get; private set; } = new List<CurrencyRow>();

        public bool HasCurrency { get; private set; }

        public BagOutcome Bag { get; private set; } = BagOutcome.Failure("not_loaded");

        public bool HasBag { get; private set; }

        public ShopOutcome LastShop { get; private set; }

        public bool HasShop { get; private set; }

        public AuctionOutcome LastAuction { get; private set; }

        public bool HasAuction { get; private set; }

        public IngotOutcome LastIngot { get; private set; }

        public bool HasIngot { get; private set; }

        public RechargeOrderOutcome LastOrder { get; private set; }

        public bool HasOrder { get; private set; }

        public RechargeSettleOutcome LastSettle { get; private set; }

        public bool HasSettle { get; private set; }

        public string Error { get; private set; } = string.Empty;

        /// <summary>最近一笔挂单编号（拍卖或寄售），购买时用它。</summary>
        public ulong LastListingId { get; private set; }

        public static UiCommerceFlow Ensure(UiShell shell)
        {
            var existing = FindObjectOfType<UiCommerceFlow>();
            if (existing != null)
            {
                existing._shell = shell;
                return existing;
            }

            var go = new GameObject("UiCommerceFlow");
            var flow = go.AddComponent<UiCommerceFlow>();
            flow._shell = shell;
            flow._send = flow.SendByUnityWebRequest;
            Debug.Log(ReadyLog);
            return flow;
        }

        public void SetSender(CommerceSend send)
        {
            _send = send;
        }

        public void Bind(string token, ulong characterId)
        {
            _token = token ?? string.Empty;
            _characterId = characterId;
        }

        public ulong CharacterId => _characterId;

        public void LoadAll(Action done = null)
        {
            LoadCurrency(() => LoadBag(done));
        }

        public void LoadCurrency(Action done = null)
        {
            if (_characterId == 0)
            {
                HasCurrency = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterCurrency.RequestPath(_characterId), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    Balances = CharacterCurrency.Parse(text);
                    HasCurrency = true;
                    Debug.Log("u6_currency=ok rows=" + Balances.Count);
                }
                else
                {
                    Error = "货币没读到：" + status;
                    Debug.Log("u6_currency=reject status=" + status);
                }

                if (done != null) done();
            });
        }

        public void LoadBag(Action done = null)
        {
            if (_characterId == 0)
            {
                HasBag = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterBag.RequestPath(_characterId), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    Bag = CharacterBag.Parse(text);
                    HasBag = Bag.Ok;
                    Debug.Log("u6_bag=ok slots=" + (Bag.Slots != null ? Bag.Slots.Count : 0));
                }
                else
                {
                    Error = "背包没读到：" + status;
                    Debug.Log("u6_bag=reject status=" + status);
                }

                if (done != null) done();
            });
        }

        public void Buy(string itemId, Action done = null)
        {
            Send<ShopOutcome>("/shop/buy", CharacterShop.RequestBody(_characterId, itemId),
                (status, text) => status == 200
                    ? CharacterShop.Parse(text)
                    : new ShopOutcome { Reason = "status_" + status, DisplayText = "没发出去：" + status },
                outcome => { LastShop = outcome; HasShop = true; },
                "u6_shop", done);
        }

        public void AuctionList(uint slot, uint count, long priceGold, Action done = null)
        {
            Send<AuctionOutcome>("/auction/list",
                CharacterAuction.ListBody(_characterId, slot, count, priceGold),
                (status, text) => status == 200
                    ? CharacterAuction.Parse(text)
                    : new AuctionOutcome { Reason = "status_" + status, DisplayText = "没发出去：" + status },
                outcome =>
                {
                    LastAuction = outcome;
                    HasAuction = true;
                    if (outcome.ListingId != 0) LastListingId = outcome.ListingId;
                },
                "u6_auction", done);
        }

        public void AuctionBuy(ulong listingId, Action done = null)
        {
            Send<AuctionOutcome>("/auction/buy",
                CharacterAuction.BuyBody(_characterId, listingId),
                (status, text) => status == 200
                    ? CharacterAuction.Parse(text)
                    : new AuctionOutcome { Reason = "status_" + status, DisplayText = "没发出去：" + status },
                outcome => { LastAuction = outcome; HasAuction = true; },
                "u6_auction_buy", done);
        }

        public void IngotList(long ingotAmount, long priceGold, Action done = null)
        {
            Send<IngotOutcome>("/auction/ingot/list",
                CharacterAuction.IngotListBody(_characterId, ingotAmount, priceGold),
                (status, text) => status == 200
                    ? CharacterAuction.ParseIngot(text)
                    : new IngotOutcome { Reason = "status_" + status, DisplayText = "没发出去：" + status },
                outcome =>
                {
                    LastIngot = outcome;
                    if (outcome.ListingId != 0) LastListingId = outcome.ListingId;
                    HasIngot = true;
                },
                "u6_ingot", done);
        }

        public void IngotBuy(ulong listingId, Action done = null)
        {
            Send<IngotOutcome>("/auction/ingot/buy",
                CharacterAuction.IngotBuyBody(_characterId, listingId),
                (status, text) => status == 200
                    ? CharacterAuction.ParseIngot(text)
                    : new IngotOutcome { Reason = "status_" + status, DisplayText = "没发出去：" + status },
                outcome => { LastIngot = outcome; HasIngot = true; },
                "u6_ingot_buy", done);
        }

        public void RechargeOrder(string productId, Action done = null)
        {
            Send<RechargeOrderOutcome>("/recharge/order",
                CharacterRecharge.OrderBody(_characterId, productId),
                (status, text) => status == 200
                    ? CharacterRecharge.ParseOrder(text)
                    : new RechargeOrderOutcome { Reason = "status_" + status, DisplayText = "没发出去：" + status },
                outcome => { LastOrder = outcome; HasOrder = true; },
                "u6_recharge_order", done);
        }

        public void RechargeCallback(string orderId, Action done = null)
        {
            Send<RechargeSettleOutcome>("/recharge/callback",
                CharacterRecharge.CallbackBody(orderId),
                (status, text) => status == 200
                    ? CharacterRecharge.ParseSettle(text)
                    : new RechargeSettleOutcome { Reason = "status_" + status, DisplayText = "没发出去：" + status },
                outcome => { LastSettle = outcome; HasSettle = true; },
                "u6_recharge_callback", done);
        }

        void Send<T>(string path, string body,
            Func<int, string, T> parse, Action<T> keep, string logTag, Action done)
        {
            if (_characterId == 0)
            {
                return;
            }

            Busy = true;
            Requests++;
            _send(path, body, _token, (status, text) =>
            {
                Busy = false;
                var outcome = parse(status, text);
                keep(outcome);
                Error = status == 200 ? string.Empty : "请求失败：" + status;
                Debug.Log(logTag + "=" + (status == 200 ? "ok" : "reject") + " status=" + status);
                if (done != null) done();
            });
        }

        void SendByUnityWebRequest(string path, string body, string token, Action<int, string> done)
        {
            var isGet = string.IsNullOrEmpty(body);
            var request = new UnityWebRequest(
                Gateway + path, isGet ? UnityWebRequest.kHttpVerbGET : UnityWebRequest.kHttpVerbPOST);
            if (!isGet)
            {
                request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
                request.SetRequestHeader("Content-Type", "application/json");
            }

            if (!string.IsNullOrEmpty(token))
            {
                request.SetRequestHeader("Authorization", "Bearer " + token);
            }

            request.downloadHandler = new DownloadHandlerBuffer();
            request.SendWebRequest().completed += _ =>
            {
                var status = (int)request.responseCode;
                var text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                done(status, text);
                request.Dispose();
            };
        }
    }
}

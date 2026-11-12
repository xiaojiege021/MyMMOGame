using System;
using System.Collections.Generic;
using Mmo.Net;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U5 角色养成七屏的流程与唯一判定出口（原型 D01–D07）。
    ///
    /// 这一层只做三件事：把请求发出去、把回包拆成字段、把字段交给屏。
    /// 强化成功率、费用、等级、绑定、镶嵌位、背包分类一条都不在这里算——
    /// 屏上出现的数要么来自 `/inventory`，要么来自 `/character/enhance`、
    /// `/character/transfer`、`/character/crystal/*` 的回包；没收到回包就写「尚未请求」。
    ///
    /// 与 U3 / U4 同一个形状：发送可以替换（<see cref="SetSender"/>），
    /// 编辑器自检拿它喂假回包，真机走 <see cref="SendByUnityWebRequest"/>。
    /// </summary>
    public sealed class UiUpgradeFlow : MonoBehaviour
    {
        /// <summary>这一层建好的日志行。</summary>
        public const string ReadyLog = "u5_upgrade=ready";

        /// <summary>网关地址。真机用 `adb reverse` 把 61000 转出来。</summary>
        public static string Gateway = "http://127.0.0.1:61000";

        /// <summary>发一次请求。参数是路径、请求体（空串表示 GET）、令牌、回调。</summary>
        public delegate void UpgradeSend(string path, string body, string token, Action<int, string> done);

        UiShell _shell;

        UpgradeSend _send;

        string _token = string.Empty;

        ulong _characterId;

        /// <summary>正在等回包。等的时候按钮不可按，免得连点。</summary>
        public bool Busy { get; private set; }

        /// <summary>发过几次请求。核对拿它判断「没联网时也确实没发」。</summary>
        public int Requests { get; private set; }

        /// <summary>背包回包。没发过就是 default，屏上照「尚未读背包」显示。</summary>
        public BagOutcome Bag { get; private set; } = BagOutcome.Failure("not_loaded");

        public bool HasBag { get; private set; }

        public EnhanceOutcome LastEnhance { get; private set; }

        public bool HasEnhance { get; private set; }

        public TransferOutcome LastTransfer { get; private set; }

        public bool HasTransfer { get; private set; }

        public CrystalOutcome LastCrystal { get; private set; }

        public bool HasCrystal { get; private set; }

        /// <summary>最近一条失败说明（没到网关 / 令牌失效 / 服务端 5xx）。</summary>
        public string Error { get; private set; } = string.Empty;

        /// <summary>取一份流程，没有就建。</summary>
        public static UiUpgradeFlow Ensure(UiShell shell)
        {
            var existing = FindObjectOfType<UiUpgradeFlow>();
            if (existing != null)
            {
                existing._shell = shell;
                return existing;
            }

            var go = new GameObject("UiUpgradeFlow");
            var flow = go.AddComponent<UiUpgradeFlow>();
            flow._shell = shell;
            flow._send = flow.SendByUnityWebRequest;
            Debug.Log(ReadyLog);
            return flow;
        }

        /// <summary>换掉发送实现。自检与真机共用同一条解析路径。</summary>
        public void SetSender(UpgradeSend send)
        {
            _send = send;
        }

        /// <summary>绑定会话。U2 登入后把令牌与角色号带过来。</summary>
        public void Bind(string token, ulong characterId)
        {
            _token = token ?? string.Empty;
            _characterId = characterId;
        }

        public ulong CharacterId => _characterId;

        public bool HasSession => _characterId != 0;

        // ------------------------------------------------------------------ 动作

        /// <summary>读背包。界面进 U5 时先调一次。</summary>
        public void LoadBag(Action done = null)
        {
            if (_characterId == 0)
            {
                Bag = BagOutcome.Failure("no_character");
                HasBag = false;
                Debug.Log("u5_bag=reject reason=no_character");
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterBag.RequestPath(_characterId), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status != 200)
                {
                    Bag = BagOutcome.Failure("status_" + status);
                    HasBag = false;
                    Error = "背包没读到：" + status;
                    Debug.Log("u5_bag=reject status=" + status);
                }
                else
                {
                    Bag = CharacterBag.Parse(text);
                    HasBag = Bag.Ok;
                    Error = Bag.Ok ? string.Empty : "背包回包读不出来";
                    Debug.Log("u5_bag=" + (Bag.Ok ? "ok" : "reject")
                        + " slots=" + (Bag.Slots != null ? Bag.Slots.Count : 0)
                        + " bag=" + Bag.BagSlots
                        + " name=" + Bag.CharacterName
                        + " realm=" + Bag.Realm);
                }

                if (done != null) done();
            });
        }

        /// <summary>强化一格。石头编号只作请求参数，成不成、掉几级、扣多少全看回包。</summary>
        public void Enhance(uint slot, string stoneId, bool useCoupon, Action done = null)
        {
            if (_characterId == 0)
            {
                LastEnhance = new EnhanceOutcome { Reason = "no_character", DisplayText = "还没有角色" };
                HasEnhance = true;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            var body = CharacterEnhance.RequestBody(_characterId, slot, stoneId, useCoupon);
            _send("/character/enhance", body, _token, (status, text) =>
            {
                Busy = false;
                if (status != 200)
                {
                    LastEnhance = new EnhanceOutcome { Reason = "status_" + status, DisplayText = "强化没发出去：" + status };
                    Error = LastEnhance.DisplayText;
                    Debug.Log("u5_enhance=reject status=" + status);
                }
                else
                {
                    LastEnhance = CharacterEnhance.Parse(text);
                    Error = LastEnhance.Ok ? string.Empty : LastEnhance.DisplayText;
                    Debug.Log("u5_enhance=" + (LastEnhance.Ok ? "ok" : "reject")
                        + " before=" + LastEnhance.LevelBefore
                        + " level=" + LastEnhance.Level
                        + " success=" + (LastEnhance.Success ? "true" : "false")
                        + " reason=" + LastEnhance.Reason);
                }

                HasEnhance = true;
                if (done != null) done();
            });
        }

        /// <summary>把强化从一格转到另一格。</summary>
        public void Transfer(uint fromSlot, uint toSlot, Action done = null)
        {
            if (_characterId == 0)
            {
                LastTransfer = new TransferOutcome { Reason = "no_character", DisplayText = "还没有角色" };
                HasTransfer = true;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            var body = CharacterTransfer.RequestBody(_characterId, fromSlot, toSlot);
            _send("/character/transfer", body, _token, (status, text) =>
            {
                Busy = false;
                if (status != 200)
                {
                    LastTransfer = new TransferOutcome { Reason = "status_" + status, DisplayText = "转移没发出去：" + status };
                    Error = LastTransfer.DisplayText;
                    Debug.Log("u5_transfer=reject status=" + status);
                }
                else
                {
                    LastTransfer = CharacterTransfer.Parse(text);
                    Error = LastTransfer.Ok ? string.Empty : LastTransfer.DisplayText;
                    Debug.Log("u5_transfer=" + (LastTransfer.Ok ? "ok" : "reject")
                        + " from=" + LastTransfer.FromLevel
                        + " to=" + LastTransfer.ToLevel
                        + " reason=" + LastTransfer.Reason);
                }

                HasTransfer = true;
                if (done != null) done();
            });
        }

        /// <summary>装上结晶。部位、类别、镶嵌位数量都看回包。</summary>
        public void InstallCrystal(uint slot, uint crystalSlot, uint socket, Action done = null)
        {
            CrystalRequest("/character/crystal/install",
                CharacterCrystal.InstallBody(_characterId, slot, crystalSlot, socket), done);
        }

        /// <summary>拆下结晶。可拆卸复用，拆下不改强化、金币与绑定。</summary>
        public void RemoveCrystal(uint slot, uint socket, Action done = null)
        {
            CrystalRequest("/character/crystal/remove",
                CharacterCrystal.RemoveBody(_characterId, slot, socket), done);
        }

        void CrystalRequest(string path, string body, Action done)
        {
            if (_characterId == 0)
            {
                LastCrystal = new CrystalOutcome { Reason = "no_character", DisplayText = "还没有角色" };
                HasCrystal = true;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(path, body, _token, (status, text) =>
            {
                Busy = false;
                if (status != 200)
                {
                    LastCrystal = new CrystalOutcome { Reason = "status_" + status, DisplayText = "结晶请求没发出去：" + status };
                    Error = LastCrystal.DisplayText;
                    Debug.Log("u5_crystal=reject status=" + status);
                }
                else
                {
                    LastCrystal = CharacterCrystal.Parse(text);
                    Error = LastCrystal.Ok ? string.Empty : LastCrystal.DisplayText;
                    Debug.Log("u5_crystal=" + (LastCrystal.Ok ? "ok" : "reject")
                        + " installed=" + (LastCrystal.Installed ? "true" : "false")
                        + " socket=" + LastCrystal.Socket
                        + " filled=" + LastCrystal.Filled
                        + " reason=" + LastCrystal.Reason);
                }

                HasCrystal = true;
                if (done != null) done();
            });
        }

        // ------------------------------------------------------------------ 真机发送

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
                if (request.result != UnityWebRequest.Result.Success && status == 0)
                {
                    status = 0;
                }

                done(status, text);
                request.Dispose();
            };
        }
    }
}

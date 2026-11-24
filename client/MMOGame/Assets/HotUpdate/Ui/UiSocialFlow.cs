using System;
using System.Collections.Generic;
using System.Globalization;
using Mmo.Net;
using Mmo.Proto.Battle;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>一个城战单位在屏上该画成什么样。编号与血量都是服务端下发的。</summary>
    public struct SiegeRow
    {
        public ulong MemberId;
        public uint Team;
        public long Hp;
        public long HpMax;
        public SiegeVisualSpec Visual;
    }

    /// <summary>
    /// U7 社交与组织五屏的流程与唯一判定出口（原型 F01–F05）。
    ///
    /// 只做三件事：发请求、拆回包、把字段交给屏。名册上限、等级、席位、建设、
    /// 掌门、世界 Boss 归属、城战同屏档位、洞府摆放、图鉴收集度与被动、竞技场与战场的
    /// 进场路径一条都不在这里算——要么来自 `/sect/*`、`/world/worldboss`、`/homestead*`、
    /// `/hero-codex`、`/versus/*`，要么来自战斗服下发的 `SiegeView`。
    /// </summary>
    public sealed partial class UiSocialFlow : MonoBehaviour
    {
        public const string ReadyLog = "u7_social=ready";

        public static string Gateway = "http://127.0.0.1:61000";

        public delegate void SocialSend(string path, string body, string token, Action<int, string> done);

        UiShell _shell;

        SocialSend _send;

        string _token = string.Empty;

        ulong _characterId;

        public bool Busy { get; private set; }

        public int Requests { get; private set; }

        public SectOutcome LastSect { get; private set; }

        public bool HasSect { get; private set; }

        public PositionOutcome LastPosition { get; private set; }

        public bool HasPosition { get; private set; }

        public LeadershipOutcome LastLeadership { get; private set; }

        public bool HasLeadership { get; private set; }

        public UpgradeOutcome LastUpgrade { get; private set; }

        public bool HasUpgrade { get; private set; }

        public RosterOutcome LastRoster { get; private set; }

        public bool HasRoster { get; private set; }

        public WorldBossFacts LastBoss { get; private set; }

        public bool HasBoss { get; private set; }

        public bool HasSiege { get; private set; }

        public uint SiegeFullLimit { get; private set; }

        public uint SiegeFullCount { get; private set; }

        public uint SiegeUnitCount { get; private set; }

        public List<SiegeRow> SiegeRows { get; private set; } = new List<SiegeRow>();

        public HomesteadOutcome LastHomestead { get; private set; }

        public bool HasHomestead { get; private set; }

        public BagOutcome HomesteadBag { get; private set; } = BagOutcome.Failure("not_loaded");

        public bool HasHomesteadBag { get; private set; }

        // ------------------------------------------------------------------ 洞府种植（U10 / N11）

        public HomesteadPlantOutcome LastPlants { get; private set; }

        public bool HasPlants { get; private set; }

        /// <summary>最近一次种下 / 收取的回包。</summary>
        public HomesteadPlantOutcome LastPlantAction { get; private set; }

        public bool HasPlantAction { get; private set; }

        /// <summary>回包里的家园币余额。只有收取与商城回包带这个数，别的回包不冒充余额。</summary>
        public long HomeCoinBalance { get; private set; }

        public bool HasHomeCoinBalance { get; private set; }

        public HomeCoinShopOutcome LastHomeCoin { get; private set; }

        public bool HasHomeCoin { get; private set; }

        // ------------------------------------------------------------------ 英灵图鉴（U11 / N12）

        /// <summary>最近一次 `/hero-codex` 回包。九格名字、收集度与被动万分比都从这里取。</summary>
        public HeroCodexOutcome LastCodex { get; private set; }

        public bool HasCodex { get; private set; }

        /// <summary>这条图鉴口发过请求没有。发过但没拿到 200 时，屏上写回执而不是「尚未请求」。</summary>
        public bool CodexTried { get; private set; }

        // ------------------------------------------------------------------ 竞技场与战场进场（U12 / N5）

        /// <summary>最近一次 `POST /versus/arena/enter` 的回包。进场路径、模式与人数都从这里取。</summary>
        public VersusOutcome LastArena { get; private set; }

        public bool HasArena { get; private set; }

        /// <summary>竞技场这条口发过请求没有。发过但没拿到 200 时，屏上写回执而不是「尚未请求」。</summary>
        public bool ArenaTried { get; private set; }

        /// <summary>最近一次 `POST /versus/battlefield/enter` 的回包。场次与队伍都从这里取。</summary>
        public VersusOutcome LastBattlefield { get; private set; }

        public bool HasBattlefield { get; private set; }

        public bool BattlefieldTried { get; private set; }

        public string Error { get; private set; } = string.Empty;

        /// <summary>最近一次回包给的宗门编号。没有回包就是 0，屏上不猜一个。</summary>
        public ulong SectId
        {
            get
            {
                if (LastSect.SectId != 0)
                {
                    return LastSect.SectId;
                }

                if (LastPosition.SectId != 0)
                {
                    return LastPosition.SectId;
                }

                if (LastLeadership.SectId != 0)
                {
                    return LastLeadership.SectId;
                }

                return LastUpgrade.SectId;
            }
        }

        public static UiSocialFlow Ensure(UiShell shell)
        {
            var existing = FindObjectOfType<UiSocialFlow>();
            if (existing != null)
            {
                existing._shell = shell;
                return existing;
            }

            var go = new GameObject("UiSocialFlow");
            var flow = go.AddComponent<UiSocialFlow>();
            flow._shell = shell;
            flow._send = flow.SendByUnityWebRequest;
            Debug.Log(ReadyLog);
            return flow;
        }

        public void SetSender(SocialSend send)
        {
            _send = send;
        }

        public void Bind(string token, ulong characterId)
        {
            _token = token ?? string.Empty;
            _characterId = characterId;
        }

        public ulong CharacterId => _characterId;
        // ------------------------------------------------------------------ 发送

        void Send<T>(string path, string body,
            Func<string, T> parse, Action<T> keep, string logTag, Action done)
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
                if (status == 200)
                {
                    var outcome = parse(text);
                    keep(outcome);
                    Error = string.Empty;
                    Debug.Log(logTag + "=ok");
                }
                else
                {
                    Error = UiStrings.UiSocialFlow.T001 + status;
                    Debug.Log(logTag + "=reject status=" + status);
                }

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

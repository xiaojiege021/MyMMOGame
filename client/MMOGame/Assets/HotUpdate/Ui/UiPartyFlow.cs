using System;
using Mmo.Net;
using UnityEngine;
using UnityEngine.Networking;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U14 组队流程：只接社交服已有的七个 Party RPC（网关 `GET /party`、
    /// `POST /party/{invite,respond,ready/start,ready,leave,disband}`）。
    ///
    /// 一条判定都不做：能不能邀请、队伍满没满、准备超时没有、谁当队长、分配什么口径，
    /// 全部由社交服按 `config/party.json` 判，回包里是什么就显示什么。
    /// 与 U3 的 `UiDungeonFlow` / `UiPartyScreen` 是两条线：那边读战斗服的同场名单，这里读队伍状态。
    /// </summary>
    public sealed class UiPartyFlow : MonoBehaviour
    {
        public const string ReadyLog = "u14_party=ready";

        public static string Gateway = "http://127.0.0.1:61000";

        public delegate void PartySend(string path, string body, string token, Action<int, string> done);

        PartySend _send;

        UiShell _shell;

        string _token = string.Empty;

        ulong _characterId;

        /// <summary>最近一次队伍回包。成员、队长、准备态、分配口径都从这里取。</summary>
        public PartyOutcome Last { get; private set; }

        public bool HasReply { get; private set; }

        /// <summary>这条口发过请求没有。发过但没拿到 200 时，屏上写回执而不是「还没到」。</summary>
        public bool Tried { get; private set; }

        public bool Busy { get; private set; }

        public int Requests { get; private set; }

        public string Error { get; private set; } = string.Empty;

        public ulong CharacterId => _characterId;

        public static UiPartyFlow Ensure(UiShell shell)
        {
            var existing = FindObjectOfType<UiPartyFlow>();
            if (existing != null)
            {
                existing._shell = shell;
                return existing;
            }

            var go = new GameObject("UiPartyFlow");
            var flow = go.AddComponent<UiPartyFlow>();
            flow._shell = shell;
            flow._send = flow.SendByUnityWebRequest;
            Debug.Log(ReadyLog);
            return flow;
        }

        public void SetSender(PartySend send)
        {
            _send = send;
        }

        public void Bind(string token, ulong characterId)
        {
            _token = token ?? string.Empty;
            _characterId = characterId;
        }

        // ------------------------------------------------------------------ 七个口

        /// <summary>只读：现在这一队在不在、几个人、谁队长、什么分配口径。请求体为空，走 GET。</summary>
        public void Load(Action done = null)
        {
            if (_characterId == 0)
            {
                return;
            }

            Send(CharacterParty.DescribeQuery(_characterId), string.Empty, "u14_party_describe", done);
        }

        public void Invite(ulong targetCharacterId, Action done = null)
        {
            Send(CharacterParty.InvitePath, CharacterParty.InviteBody(_characterId, targetCharacterId),
                "u14_party_invite", done);
        }

        public void Respond(ulong fromCharacterId, bool accept, Action done = null)
        {
            Send(CharacterParty.RespondPath, CharacterParty.RespondBody(_characterId, fromCharacterId, accept),
                "u14_party_respond", done);
        }

        public void StartReady(Action done = null)
        {
            Send(CharacterParty.ReadyStartPath, CharacterParty.MemberBody(_characterId),
                "u14_party_ready_start", done);
        }

        public void SetReady(bool ready, Action done = null)
        {
            Send(CharacterParty.ReadyPath, CharacterParty.ReadyBody(_characterId, ready),
                "u14_party_ready", done);
        }

        public void Leave(Action done = null)
        {
            Send(CharacterParty.LeavePath, CharacterParty.MemberBody(_characterId),
                "u14_party_leave", done);
        }

        public void Disband(Action done = null)
        {
            Send(CharacterParty.DisbandPath, CharacterParty.MemberBody(_characterId),
                "u14_party_disband", done);
        }

        // ------------------------------------------------------------------ 发送

        void Send(string path, string body, string logTag, Action done)
        {
            if (_characterId == 0)
            {
                return;
            }

            Busy = true;
            Tried = true;
            Requests++;
            _send(path, body, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    Last = CharacterParty.Parse(text);
                    HasReply = true;
                    Error = string.Empty;
                    Debug.Log(logTag + "=ok in_party=" + (Last.InParty ? "true" : "false")
                        + " size=" + Last.Size + "/" + Last.MaxSize
                        + " loot=" + (Last.LootMode.Length == 0 ? "none" : Last.LootMode));
                }
                else
                {
                    Error = UiStrings.UiPartyFlow.T001 + status;
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

using System;
using System.Collections;
using System.Collections.Generic;
using Mmo.Net;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 战斗与副本四屏的流程与唯一判定出口（原型 C01–C04）。
    ///
    /// 这一层只做三件事：把进本请求发出去、把回包拆成人能读的字段、把字段交给屏。
    /// 能不能进、扣多少疲劳、占多少份额、每人几份，一条都不在这里算——
    /// 屏上出现的数字要么来自 `/dungeon/enter` 回包，要么来自战斗服的 `Ended` 回包，
    /// 没有第三种来源。没收到回包时屏上写「尚未进本」，不拿配置里的顶格值假装。
    ///
    /// 与 U2 的入口流程同一个形状：发送可以替换（<see cref="SetSender"/>），
    /// 编辑器自检拿它喂假回包，真机走 <see cref="SendByUnityWebRequest"/>。
    /// </summary>
    public sealed class UiDungeonFlow : MonoBehaviour
    {
        /// <summary>这一层建好的日志行。</summary>
        public const string ReadyLog = "u3_dungeon=ready";

        /// <summary>网关地址。真机用 `adb reverse` 把 61000 转出来。</summary>
        public static string Gateway = "http://127.0.0.1:61000";

        /// <summary>发一次请求。参数是路径、请求体、令牌、回调（状态码，回包正文）。</summary>
        public delegate void EnterSend(string path, string body, string token, Action<int, string> done);

        /// <summary>屏上要显示的一位队友。全部字段来自战斗服的名单回包。</summary>
        public struct PartyRow
        {
            public string Name;
            public string ClassId;
            public long Hp;
            public long HpMax;
            public bool Down;
        }

        UiShell _shell;

        EnterSend _send;

        string _token = string.Empty;

        ulong _characterId;

        readonly List<UiOption> _tabs = new List<UiOption>();

        Text _sizeText;

        Text _resultText;

        Text _hintText;

        UiButton _enterButton;

        Text _settleState;

        Text _settleHead;

        Text _settleExp;

        Text _settleCost;

        Text _settleDrop;

        /// <summary>正在等回包。等的时候按钮不可按，免得连点发两次。</summary>
        public bool Busy { get; private set; }

        /// <summary>最近一次进本回包。没发过就是 default，界面照「尚未进本」显示。</summary>
        public EnterOutcome Last { get; private set; }

        /// <summary>发过几次请求。核对拿它判断「没联网时也确实没发」。</summary>
        public int Requests { get; private set; }

        /// <summary>是否已经收到过一次回包。</summary>
        public bool HasReply { get; private set; }

        /// <summary>当前选中的副本编号。默认深渊，与原型一致。</summary>
        public string ChosenId { get; private set; } = "abyss";

        /// <summary>进本人数。只能在 1 到这张副本的 max_players 之间动。</summary>
        public int PartySize { get; private set; } = 1;

        /// <summary>结算屏有没有拿到过战斗服的 Ended 回包。</summary>
        public bool HasSettlement { get; private set; }

        public bool Cleared { get; private set; }

        public string SettlementId { get; private set; } = string.Empty;

        public long ExpGain { get; private set; }

        public int SettlePartySize { get; private set; }

        public int SettleRewardShares { get; private set; }

        /// <summary>小队屏的名单。默认由调用方从战斗服名单灌进来，自检可以喂假的。</summary>
        public readonly List<PartyRow> Party = new List<PartyRow>();

        /// <summary>屏上此刻写着什么。自检拿它跟回包逐字比，不另存一份副本。</summary>
        public string ScreenSizeText => _sizeText != null ? _sizeText.text : string.Empty;

        public string ScreenResultText => _resultText != null ? _resultText.text : string.Empty;

        public string ScreenHintText => _hintText != null ? _hintText.text : string.Empty;

        public string ScreenSettleState => _settleState != null ? _settleState.text : string.Empty;

        public string ScreenSettleExp => _settleExp != null ? _settleExp.text : string.Empty;

        public string ScreenSettleDrop => _settleDrop != null ? _settleDrop.text : string.Empty;

        public string ScreenSettleCost => _settleCost != null ? _settleCost.text : string.Empty;

        public string ScreenSettleHead => _settleHead != null ? _settleHead.text : string.Empty;

        /// <summary>进本按钮现在能不能按。等回包时不可按，未开放副本也不可按。</summary>
        public bool EnterEnabled => _enterButton != null && _enterButton.Enabled;

        /// <summary>挂到骨架上。已有就复用，不重复建。</summary>
        public static UiDungeonFlow Ensure(UiShell shell)
        {
            var existing = shell.GetComponent<UiDungeonFlow>();
            if (existing != null)
            {
                existing.Init(shell);
                return existing;
            }

            var flow = shell.gameObject.AddComponent<UiDungeonFlow>();
            flow.Init(shell);
            return flow;
        }

        /// <summary>建一次。编辑模式下 `AddComponent` 不触发 `Awake`，所以这里由 `Ensure` 显式调。</summary>
        public void Init(UiShell shell)
        {
            _shell = shell;
            if (_send == null)
            {
                _send = SendByUnityWebRequest;
            }

            Debug.Log(ReadyLog + " gateway=" + Gateway + " screens=4");
        }

        /// <summary>令牌与角色号。U2 出口确认之后由入口流程交过来。</summary>
        public void Configure(string token, ulong characterId)
        {
            _token = token ?? string.Empty;
            _characterId = characterId;
        }

        /// <summary>
        /// 肉眼验收用：确保骨架与本流程在，然后开指定的一屏（`dungeon` / `settle` / `party`）。
        /// 编辑器菜单走反射调这里，不填令牌也不造假回包——屏上写的就是没回包时的样子。
        /// </summary>
        public static void Open(string which)
        {
            var shell = UiShell.Ensure();
            var flow = Ensure(shell);
            switch (which)
            {
                case "settle":
                    shell.ShowPage("战斗结算", s => UiSettleScreen.Build(s, flow));
                    break;
                case "party":
                    shell.ShowPage("小队", s => UiPartyScreen.Build(s, flow));
                    break;
                default:
                    shell.ShowPage("裂隙", s => UiDungeonScreen.Build(s, flow));
                    break;
            }

            Debug.Log("u3_preview=" + which + " gateway=" + Gateway);
        }

        /// <summary>换掉发送实现。自检与探针走这条路，不真的连网关。</summary>
        public void SetSender(EnterSend send)
        {
            _send = send ?? SendByUnityWebRequest;
        }

        /// <summary>把进本屏的控件交上来。屏建完调一次，之后由 <see cref="Refresh"/> 刷。</summary>
        public void BindDungeon(
            List<UiOption> tabs, Text sizeText, Text resultText, Text hintText, UiButton enterButton)
        {
            _tabs.Clear();
            if (tabs != null)
            {
                _tabs.AddRange(tabs);
            }

            _sizeText = sizeText;
            _resultText = resultText;
            _hintText = hintText;
            _enterButton = enterButton;
            Refresh();
        }

        /// <summary>把结算屏的控件交上来。</summary>
        public void BindSettle(
            Text state, Text head, Text exp, Text cost, Text drop)
        {
            _settleState = state;
            _settleHead = head;
            _settleExp = exp;
            _settleCost = cost;
            _settleDrop = drop;
            RefreshSettle();
        }

        /// <summary>点了一个页签。选中态只在这里改。</summary>
        public void Choose(string dungeonId)
        {
            if (Busy)
            {
                return;
            }

            UiBattleCodex.Dungeon dungeon;
            if (!UiBattleCodex.FindDungeon(dungeonId, out dungeon))
            {
                return;
            }

            ChosenId = dungeonId;
            if (PartySize > dungeon.MaxPlayers)
            {
                PartySize = dungeon.MaxPlayers;
            }

            Refresh();
        }

        /// <summary>加减人数。上下限来自配置表里这张副本的 max_players。</summary>
        public void StepParty(int delta)
        {
            if (Busy)
            {
                return;
            }

            UiBattleCodex.Dungeon dungeon;
            if (!UiBattleCodex.FindDungeon(ChosenId, out dungeon))
            {
                return;
            }

            var next = PartySize + delta;
            if (next < 1)
            {
                next = 1;
            }

            if (next > dungeon.MaxPlayers)
            {
                next = dungeon.MaxPlayers;
            }

            PartySize = next;
            Refresh();
        }

        /// <summary>
        /// 进本。请求里只有角色号、副本编号、人数；令牌走 `Authorization`。
        /// 界面不预判结果：按下就是等回包。
        /// </summary>
        public void SubmitEnter()
        {
            if (Busy)
            {
                return;
            }

            Busy = true;
            Requests++;
            Refresh();

            var body = DungeonEntry.EnterBody(_characterId, ChosenId, PartySize);
            _send("/dungeon/enter", body, _token, (status, text) =>
            {
                Busy = false;
                Last = DungeonEntry.ParseEnter(status, text);
                HasReply = true;

                Debug.Log("u3_enter=reply status=" + status
                    + " kind=" + DungeonEntry.KindName(Last.Kind)
                    + " reason=" + Last.Reason
                    + " fatigue=" + Last.Fatigue
                    + "/" + Last.FatigueDailyCap
                    + " share_left=" + Last.WeeklyShareRemaining
                    + "/" + Last.WeeklyShareCap
                    + " shares=" + Last.RewardShares
                    + " party=" + PartySize);

                Refresh();
            });
        }

        /// <summary>
        /// 战斗服的 `Ended` 回包。经验、事务号、通没通关都照抄，
        /// 界面不按血量或掉落自己推一个结果出来。
        /// </summary>
        public void SetSettlement(bool cleared, string settlementId, long expGain)
        {
            Cleared = cleared;
            SettlementId = settlementId ?? string.Empty;
            ExpGain = expGain;
            SettlePartySize = PartySize;
            SettleRewardShares = HasReply ? Last.RewardShares : 0;
            HasSettlement = true;

            Debug.Log("u3_settle=reply cleared=" + (cleared ? "true" : "false")
                + " settlement=" + SettlementId
                + " exp=" + ExpGain
                + " party=" + SettlePartySize
                + " shares=" + SettleRewardShares);

            RefreshSettle();
        }

        /// <summary>小队名单。战斗服下发的同场成员原样放进来，不自造队友也不补位。</summary>
        public void SetParty(IEnumerable<PartyRow> rows)
        {
            Party.Clear();
            if (rows != null)
            {
                Party.AddRange(rows);
            }
        }

        /// <summary>刷新进本屏。所有文案都是从字段直接拼出来的，没有一处再算一遍。</summary>
        public void Refresh()
        {
            UiBattleCodex.Dungeon dungeon;
            var known = UiBattleCodex.FindDungeon(ChosenId, out dungeon);

            for (var i = 0; i < _tabs.Count; i++)
            {
                _tabs[i].Selected = _tabs[i].Id == ChosenId;
                _tabs[i].Apply(_shell.Palette);
            }

            if (_sizeText != null)
            {
                _sizeText.text = known
                    ? "进本人数 " + PartySize + " / 上限 " + dungeon.MaxPlayers
                    : "进本人数 " + PartySize;
            }

            if (_resultText != null)
            {
                _resultText.text = ResultLine();
            }

            if (_hintText != null)
            {
                _hintText.text = HintLine();
            }

            if (_enterButton != null)
            {
                _enterButton.Enabled = !Busy && known;
            }
        }

        /// <summary>进本屏底部那行。没回包就写「尚未进本」，不拿配置值假装成已确认。</summary>
        public string ResultLine()
        {
            if (!HasReply)
            {
                return "尚未进本 · 疲劳与份额以回包为准";
            }

            if (Last.Kind != EnterKind.Allowed && Last.Kind != EnterKind.Refused)
            {
                return "进本没发出去 · " + DungeonEntry.KindName(Last.Kind);
            }

            return "疲劳 " + Last.Fatigue + " / " + Last.FatigueDailyCap
                + " · 本次扣 " + Last.FatigueCost
                + " · 本周剩 " + Last.WeeklyShareRemaining + " / " + Last.WeeklyShareCap
                + " · 每人 " + Last.RewardShares + " 份";
        }

        /// <summary>被拒原因那一行。能进与没回包时都留空，不占屏。</summary>
        public string HintLine()
        {
            if (!HasReply)
            {
                return string.Empty;
            }

            switch (Last.Kind)
            {
                case EnterKind.Allowed:
                    return "已进本 · 记录 " + (Last.EntryId.Length > 0 ? Last.EntryId : "—");
                case EnterKind.Refused:
                    return "进不去 · " + UiBattleCodex.ReasonText(Last.Reason);
                case EnterKind.InvalidToken:
                    return "令牌失效 · 回登录重来";
                case EnterKind.Network:
                    return "连不上网关 · 检查网络再试";
                case EnterKind.Unavailable:
                    return "服务端暂时连不上库 · 稍后再试";
                case EnterKind.BadRequest:
                    return "请求没发出去 · 人数或副本编号不对";
                default:
                    return "服务端拒了这次进本 · " + Last.Error;
            }
        }

        /// <summary>刷新结算屏。通关与否、经验、事务号都来自 Ended 回包。</summary>
        public void RefreshSettle()
        {
            if (_settleState != null)
            {
                _settleState.text = HasSettlement ? (Cleared ? "通 关" : "未 通 关") : "未 结 算";
            }

            if (_settleHead != null)
            {
                if (!HasSettlement)
                {
                    _settleHead.text = "等战斗服的结算回包";
                }
                else
                {
                    var name = "副本";
                    UiBattleCodex.Dungeon dungeon;
                    if (UiBattleCodex.FindDungeon(ChosenId, out dungeon))
                    {
                        name = dungeon.Name;
                    }

                    _settleHead.text = name + " · " + SettlePartySize + " 人 · 每人 " + SettleRewardShares + " 份";
                }
            }

            if (_settleExp != null)
            {
                _settleExp.text = HasSettlement
                    ? "等级经验 +" + ExpGain + "（回包给的数，本地不加成、不翻倍）"
                    : "等结算回包";
            }

            if (_settleCost != null)
            {
                _settleCost.text = HasReply
                    ? "本次扣疲劳 " + Last.FatigueCost + " · 进本后疲劳 " + Last.Fatigue
                        + " · 本周剩 " + Last.WeeklyShareRemaining + " / " + Last.WeeklyShareCap
                    : "进本回包没到，消耗口径以回包为准";
            }

            if (_settleDrop != null)
            {
                _settleDrop.text = HasSettlement && SettlementId.Length > 0
                    ? "结算事务号 " + SettlementId + " · 掉落以角色服背包回包为准"
                    : UiBattleCodex.SettleDropNote;
            }
        }

        // ------------------------------------------------------------------ 发送

        void SendByUnityWebRequest(string path, string body, string token, Action<int, string> done)
        {
            StartCoroutine(SendRoutine(path, body, token, done));
        }

        IEnumerator SendRoutine(string path, string body, string token, Action<int, string> done)
        {
            var url = Gateway + path;
            using (var request = new UnityWebRequest(url, "POST"))
            {
                var payload = System.Text.Encoding.UTF8.GetBytes(body ?? string.Empty);
                request.uploadHandler = new UploadHandlerRaw(payload);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(token))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + token);
                }

                yield return request.SendWebRequest();

                // 连接没建起来时没有 HTTP 状态，按 0 交给拆装层——那是不算「服务端拒绝」的一档。
                var status = request.result == UnityWebRequest.Result.ConnectionError ? 0 : (int)request.responseCode;
                var text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                done(status, text ?? string.Empty);
            }
        }
    }
}

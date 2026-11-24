using System;
using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U16 重连流程：重连只调 <see cref="BattleLink.ReconnectAsync"/> 回原场；
    /// 惩罚倒计时只读 `/dungeon/enter` 被拒时的 `deserter_remaining_s`（服务端事实值），
    /// 本地只按秒走针。
    ///
    /// 类里不碰系统时钟、不拿进场时刻相减、不存宽限 / 窗口时长——那些都由服务端持有。
    /// </summary>
    public sealed class UiReconnectFlow : MonoBehaviour
    {
        public const string ReadyLog = "u16_reconnect_flow=ready";

        BattleLink _link;

        UiText _countdownText;

        float _accum;

        /// <summary>服务端给过的剩余秒数。没有事实时为 0，屏上写「服务端还没说过惩罚」。</summary>
        public int Remaining { get; private set; }

        /// <summary>服务端说过一次惩罚没有。</summary>
        public bool HasFact { get; private set; }

        /// <summary>重连发过没有。</summary>
        public bool Tried { get; private set; }

        /// <summary>重连失败的原因。空串表示没失败过。</summary>
        public string Error { get; private set; } = string.Empty;

        public int Reconnects { get; private set; }

        public static UiReconnectFlow Ensure()
        {
            var existing = FindObjectOfType<UiReconnectFlow>();
            if (existing != null)
            {
                return existing;
            }

            var go = new GameObject("UiReconnectFlow");
            var flow = go.AddComponent<UiReconnectFlow>();
            Debug.Log(ReadyLog);
            return flow;
        }

        /// <summary>战斗连接。重连要经过它，界面自己不发包。</summary>
        public void Bind(BattleLink link)
        {
            _link = link;
        }

        /// <summary>屏上那一行倒计时交给这里，按秒改字。</summary>
        public void BindCountdown(UiText label)
        {
            _countdownText = label;
            RefreshCountdown();
        }

        /// <summary>
        /// 进本被拒时调这个：只抄 `deserter_remaining_s`，不拿进场时刻与宽限窗去推。
        /// 回包没带这一格就按 0 处理，屏上写「服务端还没说过惩罚」。
        /// </summary>
        public void SetFromRefusal(EnterOutcome outcome)
        {
            Remaining = outcome.DeserterRemainingS < 0 ? 0 : outcome.DeserterRemainingS;
            HasFact = true;
            _accum = 0f;
            Debug.Log("u16_deserter_remaining_s=" + Remaining);
            RefreshCountdown();
        }

        /// <summary>换一场、重新进本时清掉上一场的倒计时。事实值本身不在这算。</summary>
        public void Reset()
        {
            Remaining = 0;
            HasFact = false;
            _accum = 0f;
            Error = string.Empty;
            Tried = false;
            RefreshCountdown();
        }

        void Update()
        {
            if (Remaining > 0)
            {
                Advance(Time.deltaTime);
            }
        }

        /// <summary>
        /// 按秒走针。self-check 里直接喂秒数，与 Update 走同一条路，
        /// 保证核对过的那套算法就是运行时那套。
        /// </summary>
        public bool Advance(float seconds)
        {
            if (Remaining <= 0 || seconds <= 0f)
            {
                return false;
            }

            _accum += seconds;
            var moved = false;
            while (_accum >= 1f && Remaining > 0)
            {
                _accum -= 1f;
                Remaining--;
                moved = true;
            }

            if (moved)
            {
                RefreshCountdown();
                if (Remaining == 0)
                {
                    Debug.Log("u16_deserter_cleared");
                }
            }

            return moved;
        }

        /// <summary>重连回原场。失败只记服务端 / 连接给的原因，不自己解释。</summary>
        public async void ReconnectNow()
        {
            Tried = true;
            Error = string.Empty;
            if (_link == null)
            {
                Error = UiStrings.UiReconnectFlow.T001;
                Debug.Log("u16_reconnect=no_link");
                return;
            }

            try
            {
                var next = await _link.ReconnectAsync(TimeSpan.FromSeconds(5));
                Reconnects++;
                _link = next;
                Debug.Log("u16_reconnect=ok count=" + Reconnects);
            }
            catch (Exception error)
            {
                Error = error.GetType().Name;
                Debug.Log("u16_reconnect=fail " + Error + " " + error.Message);
            }
        }

        void RefreshCountdown()
        {
            if (_countdownText != null)
            {
                _countdownText.Label.text = UiReconnectCodex.PenaltyLine(HasFact, Remaining);
            }
        }
    }
}

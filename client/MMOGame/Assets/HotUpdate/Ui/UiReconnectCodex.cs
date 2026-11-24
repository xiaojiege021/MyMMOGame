namespace HotUpdate.Ui
{
    /// <summary>
    /// U16 重连屏的文案与口径。
    ///
    /// 两个时长不在客户端算：宽限窗与惩罚剩余都只认服务端给的值，
    /// 惩罚倒计时唯一来源是 `/dungeon/enter` 被拒时的 `deserter_remaining_s`。
    /// 界面只按秒走针，不拿进场时刻自己减。
    /// </summary>
    public static class UiReconnectCodex
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u16_reconnect=ready";

        public const string Notice = UiStrings.UiReconnectCodex.T001;

        /// <summary>服务端还没说过惩罚时的文案。</summary>
        public const string NoFact = UiStrings.UiReconnectCodex.T002;

        /// <summary>倒计时走完。</summary>
        public const string Cleared = UiStrings.UiReconnectCodex.T003;

        public const string Footer = UiStrings.UiReconnectCodex.T004;

        /// <summary>倒计时那一行。秒数原样来自回包，这里只在本地按秒往下走。</summary>
        public static string PenaltyLine(bool hasFact, int seconds)
        {
            if (!hasFact)
            {
                return NoFact;
            }

            return seconds <= 0 ? Cleared : UiStrings.UiReconnectCodex.T005 + seconds + UiStrings.UiReconnectCodex.T006;
        }

        /// <summary>重连结果那一行。成不成看服务端，客户端不解释为什么。</summary>
        public static string ReconnectLine(bool tried, string error)
        {
            if (!tried)
            {
                return UiStrings.UiReconnectCodex.T007;
            }

            return string.IsNullOrEmpty(error) ? UiStrings.UiReconnectCodex.T008 : UiStrings.UiReconnectCodex.T009 + error;
        }
    }
}

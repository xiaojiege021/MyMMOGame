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

        public const string Notice = "重连只回原场 · 惩罚倒计时只读服务端给的剩余秒数 · 界面不补算任何时间";

        /// <summary>服务端还没说过惩罚时的文案。</summary>
        public const string NoFact = "服务端还没说过惩罚";

        /// <summary>倒计时走完。</summary>
        public const string Cleared = "惩罚已走完 · 可以重新进本";

        public const string Footer = "宽限窗与窗口时长都由服务端持有 · 客户端不折时区、不按进场时刻相减";

        /// <summary>倒计时那一行。秒数原样来自回包，这里只在本地按秒往下走。</summary>
        public static string PenaltyLine(bool hasFact, int seconds)
        {
            if (!hasFact)
            {
                return NoFact;
            }

            return seconds <= 0 ? Cleared : "还剩 " + seconds + " 秒";
        }

        /// <summary>重连结果那一行。成不成看服务端，客户端不解释为什么。</summary>
        public static string ReconnectLine(bool tried, string error)
        {
            if (!tried)
            {
                return "还没重连过";
            }

            return string.IsNullOrEmpty(error) ? "回到原来那一场" : "重连没成：" + error;
        }
    }
}

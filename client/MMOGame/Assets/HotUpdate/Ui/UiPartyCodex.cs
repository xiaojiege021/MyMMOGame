namespace HotUpdate.Ui
{
    /// <summary>
    /// U14 组队屏的文案与口径。队伍是**社交服**的队伍（七个 Party RPC），
    /// 与 U3 战斗服下发的同场名单（`party` 那条战斗回包）是两回事，两处不合并、不互相补人。
    ///
    /// 这里只有字：人数上限、超时、队长是谁、几个人准备好了、分配口径，
    /// 全部来自 `GET /party` 回包，屏上一个数都不算。
    /// </summary>
    public static class UiPartyCodex
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u14_party_room=ready";

        /// <summary>没回包时的那一句。不拿本机角色凑一队。</summary>
        public const string EmptyList = UiStrings.UiPartyCodex.T001;

        public const string Notice = UiStrings.UiPartyCodex.T002;

        /// <summary>页脚口径：能做什么、什么不做。</summary>
        public const string Footer = UiStrings.UiPartyCodex.T003;

        /// <summary>队长那一格：队长自己不能接受别人的邀请，也不在这里转让。</summary>
        public const string LeaderHint = UiStrings.UiPartyCodex.T004;

        /// <summary>目标角色号没填时的提示。</summary>
        public const string TargetMissing = UiStrings.UiPartyCodex.T005;

        /// <summary>名额那一句：人数与上限都用回包的两个数，不写死 4。</summary>
        public static string SlotsLine(int size, int maxSize)
        {
            return maxSize <= 0 ? UiStrings.UiPartyCodex.T006 : size + " / " + maxSize + UiStrings.UiPartyCodex.T007;
        }
    }
}

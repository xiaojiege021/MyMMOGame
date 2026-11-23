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
        public const string EmptyList = "队伍名单还没到 · 队伍是社交服的事，进本之后战斗服另发一份同场名单";

        public const string Notice = "4 人是战斗单元 · 名单 / 队长 / 准备态 / 分配口径都只认 GET /party 回包";

        /// <summary>页脚口径：能做什么、什么不做。</summary>
        public const string Footer = "邀请 / 接受 / 准备 / 离队 / 解散都由社交服判，界面只发请求、只显示结果";

        /// <summary>队长那一格：队长自己不能接受别人的邀请，也不在这里转让。</summary>
        public const string LeaderHint = "队长由社交服按加入顺序继任，界面不改队长";

        /// <summary>目标角色号没填时的提示。</summary>
        public const string TargetMissing = "先填要邀请的角色号";

        /// <summary>名额那一句：人数与上限都用回包的两个数，不写死 4。</summary>
        public static string SlotsLine(int size, int maxSize)
        {
            return maxSize <= 0 ? "人数上限看回包" : size + " / " + maxSize + " 人";
        }
    }
}

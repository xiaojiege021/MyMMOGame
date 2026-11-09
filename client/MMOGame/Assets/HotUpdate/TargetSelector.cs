using System.Diagnostics;
using Mmo.Proto.Battle;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate
{
    /// <summary>
    /// 当前选中哪个目标，后续的技能请求从这里拿编号。
    /// 点选界面在热更程序集里，命中判定与结算都不在客户端算，这里只管把编号递出去。
    /// </summary>
    public static class TargetSelector
    {
        public const int SelectorVersion = 1;

        /// <summary>当前目标。刚进场景是 0，没有选中，第一次点选之后才有值。</summary>
        public static uint Current { get; private set; }

        /// <summary>切换次数。核对用它确认切换真的发生了。</summary>
        public static int SelectCount { get; private set; }

        public static bool Select(uint targetId, string displayName)
        {
            // 换目标要留日志，真机核对靠 logcat 里这两行确认点中了谁。
            Current = targetId;
            SelectCount++;
            Debug.Log("select_target=" + targetId + " name=" + displayName + " times=" + SelectCount);
            return true;
        }

        /// <summary>目标编号放进技能请求：请求里只带技能、当前目标和客户端施放时刻。</summary>
        public static SkillRequest BuildRequest(uint skillId)
        {
            return new SkillRequest
            {
                SkillId = skillId,
                TargetId = Current,
                // 单调时钟毫秒。Unity 的 Mono 没有 .NET 7 的 GetElapsedTime，用时间戳换算。
                CastTimeMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency,
            };
        }
    }
}

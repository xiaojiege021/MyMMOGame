using System.Collections.Generic;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 通用状态屏（原型 G01「状态、错误与确认」）。U1 的那一屏。
    ///
    /// 只展示：登录与会话的失败口径、确认弹窗用在哪些动作、服务端 reason 分组、
    /// 三种交互状态，以及表现层三条红线。不判定、不重试、不翻译成结论——
    /// 拒绝与否都看回包里的 <c>ok</c> 与 <c>reason</c>。
    ///
    /// 所有坐标按 <see cref="UiMetrics"/> 的设计基准来，交给骨架换算成像素；
    /// 颜色一律由色板给，这里不写色值。
    /// </summary>
    public static class UiStatusScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u1_status=ready";

        /// <summary>登录与会话那五行的 key 与说明。</summary>
        static readonly string[][] SignInRows =
        {
            new[] { "400", "请求没发出去，重试一次" },
            new[] { "401", "账号或口令不对（不区分原因）" },
            new[] { "503", "暂时连不上，稍后再试" },
            new[] { "网络不可达", "连不上网关" },
            new[] { "invalid_token / token_expired", "回登录，不弹「口令错」" },
        };

        /// <summary>三种状态样例：状态、说明、标签、标签档位。</summary>
        static readonly string[][] SampleRows =
        {
            new[] { "提交中", "按钮置灰，防重复点", "禁用" },
            new[] { "失败", "卡片内一行提示，输入保留、不清口令", "错误" },
            new[] { "成功", "Toast 一闪而过，不打断当前操作", "成功" },
        };

        /// <summary>建这一屏。骨架负责清屏与注册，这里只管排。</summary>
        public static void Build(UiShell shell)
        {
            var m = shell.Metrics;
            var left = m.Px(56f);
            var top = m.Px(150f);
            var gap = m.Px(28f);
            var contentW = m.Width - left * 2f;
            var colW = (contentW - gap) / 2f;
            var rightX = left + colW + gap;

            var signH = PanelHeight(m, SignInRows.Length);
            BuildSignIn(shell, m.Band(left, top, colW, signH));

            var confirmH = m.Px(104f) + m.Px(132f);
            BuildConfirmScope(shell, m.Band(left, top + signH + gap, colW, confirmH));

            var reasonRows = ReasonRows();
            var reasonH = PanelHeight(m, reasonRows);
            BuildReasons(shell, m.Band(rightX, top, colW, reasonH));

            var sampleH = PanelHeight(m, SampleRows.Length);
            BuildSamples(shell, m.Band(rightX, top + reasonH + gap, colW, sampleH));

            BuildFoot(shell, m);

            Debug.Log(ReadyLog + " groups=" + UiReasonCatalog.Groups.Length
                + " reasons=" + UiReasonCatalog.Count
                + " skin=" + (shell.Skin == UiSkin.Dark ? UiSkinStore.DarkValue : UiSkinStore.LightValue));
        }

        /// <summary>面板高：标题带 + 若干行 + 上下留白。</summary>
        static float PanelHeight(in UiMetrics m, int rows)
        {
            return m.Px(104f) + rows * m.Px(44f);
        }

        /// <summary>reason 面板一共几行：每组一行组名，词按每行四个折行。</summary>
        static int ReasonRows()
        {
            var rows = 0;
            for (var i = 0; i < UiReasonCatalog.Groups.Length; i++)
            {
                rows += 1;
                rows += (UiReasonCatalog.Groups[i].Reasons.Length + 3) / 4;
            }

            return rows;
        }

        static void BuildSignIn(UiShell shell, Rect rect)
        {
            var panel = shell.Panel("登录与会话", rect, shell.Size(30f));
            var pad = shell.Metrics.Px(28f);
            var rowH = shell.Metrics.Px(44f);
            var keyW = rect.width * 0.42f;
            var gap = shell.Metrics.Px(16f);
            var valW = rect.width - pad * 2f - keyW - gap;
            var y = rect.height - shell.Metrics.Px(84f);

            for (var i = 0; i < SignInRows.Length; i++)
            {
                y -= rowH;
                shell.LabelIn(panel.Root, new Rect(pad, y, keyW, rowH), SignInRows[i][0],
                    UiTextRole.Body, shell.Size(24f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(panel.Root, new Rect(pad + keyW + gap, y, valW, rowH), SignInRows[i][1],
                    UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);
            }
        }

        static void BuildConfirmScope(UiShell shell, Rect rect)
        {
            var panel = shell.Panel("确认弹窗用在哪些动作", rect, shell.Size(30f));
            var pad = shell.Metrics.Px(28f);
            shell.LabelIn(panel.Root,
                new Rect(pad, rect.height - shell.Metrics.Px(84f) - shell.Metrics.Px(132f),
                    rect.width - pad * 2f, shell.Metrics.Px(132f)),
                "强化转移（覆盖不返还）、解绑、宗门传位、离开副本。文案只陈述结果，不劝诱、不倒计时。",
                UiTextRole.Body, shell.Size(26f), TextAnchor.UpperLeft, true);
        }

        static void BuildReasons(UiShell shell, Rect rect)
        {
            var panel = shell.Panel("reason 分组（服务端正本）", rect, shell.Size(30f));
            var pad = shell.Metrics.Px(28f);
            var rowH = shell.Metrics.Px(44f);
            var width = rect.width - pad * 2f;
            var y = rect.height - shell.Metrics.Px(84f);

            for (var g = 0; g < UiReasonCatalog.Groups.Length; g++)
            {
                var group = UiReasonCatalog.Groups[g];
                y -= rowH;
                shell.LabelIn(panel.Root, new Rect(pad, y, width, rowH), group.Name,
                    UiTextRole.Jade, shell.Size(24f), TextAnchor.MiddleLeft, false);

                var line = new List<string>(4);
                for (var i = 0; i < group.Reasons.Length; i++)
                {
                    line.Add(group.Reasons[i]);
                    if (line.Count < 4)
                    {
                        continue;
                    }

                    y -= rowH;
                    shell.LabelIn(panel.Root, new Rect(pad, y, width, rowH),
                        string.Join(" · ", line.ToArray()),
                        UiTextRole.Muted, shell.Size(22f), TextAnchor.MiddleLeft, false);
                    line.Clear();
                }

                if (line.Count > 0)
                {
                    y -= rowH;
                    shell.LabelIn(panel.Root, new Rect(pad, y, width, rowH),
                        string.Join(" · ", line.ToArray()),
                        UiTextRole.Muted, shell.Size(22f), TextAnchor.MiddleLeft, false);
                }
            }
        }

        static void BuildSamples(UiShell shell, Rect rect)
        {
            var panel = shell.Panel("状态样例", rect, shell.Size(30f));
            var pad = shell.Metrics.Px(28f);
            var rowH = shell.Metrics.Px(56f);
            var chipW = shell.Metrics.Px(120f);
            var chipH = shell.Metrics.Px(40f);
            var nameW = rect.width * 0.22f;
            var descW = rect.width - pad * 2f - nameW - chipW - shell.Metrics.Px(24f);
            var y = rect.height - shell.Metrics.Px(84f);

            for (var i = 0; i < SampleRows.Length; i++)
            {
                y -= rowH;
                shell.LabelIn(panel.Root, new Rect(pad, y, nameW, rowH), SampleRows[i][0],
                    UiTextRole.Body, shell.Size(26f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(panel.Root, new Rect(pad + nameW, y, descW, rowH), SampleRows[i][1],
                    UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);

                var kind = i == 0 ? UiChipKind.Mute : i == 1 ? UiChipKind.Error : UiChipKind.Success;
                shell.Chip(panel.Root,
                    new Rect(rect.width - pad - chipW, y + (rowH - chipH) / 2f, chipW, chipH),
                    SampleRows[i][2], kind);
            }
        }

        static void BuildFoot(UiShell shell, in UiMetrics m)
        {
            shell.Label(
                new Rect(m.Px(56f), m.Height - m.Px(200f), m.Width - m.Px(112f), m.Px(64f)),
                "表现层三条红线：不发权威判定、不回传伤害数字、不做动作预测；"
                + "动画只按 foreswing_ms 拉伸播放，事件不触发命中或结算。",
                UiTextRole.Warm, shell.Size(26f), TextAnchor.MiddleLeft, true);

            var buttonW = m.Px(220f);
            var buttonH = m.Px(76f);
            var buttonY = m.Height - m.Px(116f);
            var right = m.Width - m.Px(56f);

            shell.Button(new Rect(right - buttonW * 2f - m.Px(20f), buttonY, buttonW, buttonH),
                "演示提示",
                () => shell.Toast("已保存（演示，不写任何数据）"),
                UiButtonKind.Normal);

            shell.Button(new Rect(right - buttonW, buttonY, buttonW, buttonH),
                "关闭",
                () => shell.ShowConfirm("关闭这一页？", "只回到空骨架，不影响任何数值。", "关闭", shell.ClosePage),
                UiButtonKind.Primary);
        }
    }
}

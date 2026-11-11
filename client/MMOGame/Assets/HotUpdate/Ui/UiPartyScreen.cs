using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 小队（原型 C04）。
    ///
    /// 名册与血量只认战斗服下发的同场名单（`party` 那条回包）：
    /// 名单里没有的人不列，血量比自己记的值更可信，倒地也照服务端说的画。
    /// 名单还没到就写「同场名单还没到」，不拿自己一个人凑一队。
    ///
    /// 右边只说口径：三处加成与「只加经验」的红线。加成幅度不在这里算，
    /// 结算那一笔由服务端做，本屏一个百分比都不乘。
    /// </summary>
    public static class UiPartyScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u3_party_screen=ready";

        /// <summary>建屏。返回列出来的成员数，自检拿它核对。</summary>
        public static int Build(UiShell shell, UiDungeonFlow flow)
        {
            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.54f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;

            shell.Label(new Rect(pad, m.Height - top - m.Px(40f), m.Width - pad * 2f, m.Px(40f)),
                "4 人是战斗单元，不是进本人数下限 · 名册与血量只认战斗服下发的同场名单",
                UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);

            // ---------------------------------------------------------- 左：名单

            var rowTop = top + m.Px(64f);
            var rowH = m.Px(150f);
            var rowGap = m.Px(16f);
            var shown = 0;

            if (flow.Party.Count == 0)
            {
                var empty = shell.Panel(null, m.Band(pad, rowTop, leftW, rowH), shell.Size(28f));
                shell.LabelIn(empty.Root, new Rect(m.Px(28f), m.Px(28f), leftW - m.Px(56f), m.Px(88f)),
                    "同场名单还没到 · 进本之后战斗服会把这一场的人发下来",
                    UiTextRole.Faint, shell.Size(24f), TextAnchor.MiddleLeft, true);
            }
            else
            {
                for (var i = 0; i < flow.Party.Count; i++)
                {
                    var row = flow.Party[i];
                    var band = m.Band(pad, rowTop + i * (rowH + rowGap), leftW, rowH);
                    var panel = shell.Panel(null, band, shell.Size(28f));

                    var title = ClassName(row.ClassId);
                    var name = string.IsNullOrEmpty(row.Name) ? "无名" : row.Name;
                    shell.LabelIn(panel.Root, new Rect(m.Px(28f), rowH - m.Px(72f), leftW - m.Px(200f), m.Px(48f)),
                        name + " · " + title, UiTextRole.Title, shell.Size(32f), TextAnchor.MiddleLeft, false);

                    shell.Chip(panel.Root,
                        new Rect(leftW - m.Px(148f), rowH - m.Px(66f), m.Px(120f), m.Px(42f)),
                        row.Down ? "倒地" : "在队",
                        row.Down ? UiChipKind.Error : UiChipKind.Success);

                    // 血条：宽度就是回包里的 hp / hpMax，本地不预留、不预测。
                    var barRect = new Rect(m.Px(28f), m.Px(64f), leftW - m.Px(236f), m.Px(18f));
                    DrawBar(shell, panel.Root, barRect, Ratio(row.Hp, row.HpMax));

                    shell.LabelIn(panel.Root,
                        new Rect(leftW - m.Px(200f), m.Px(52f), m.Px(172f), m.Px(40f)),
                        row.Hp + " / " + row.HpMax, UiTextRole.Muted, shell.Size(22f),
                        TextAnchor.MiddleRight, false);

                    shown++;
                }
            }

            // ---------------------------------------------------------- 右：三处加成与红线

            var boxH = m.Px(104f) + UiBattleCodex.ExpSources.Length * m.Px(64f);
            var bonus = shell.Panel("经验加成三处（都只加等级经验）",
                m.Band(rightX, rowTop, rightW, boxH), shell.Size(28f));
            var y = boxH - m.Px(96f);
            for (var i = 0; i < UiBattleCodex.ExpSources.Length; i++)
            {
                y -= m.Px(64f);
                shell.LabelIn(bonus.Root, new Rect(m.Px(28f), y, rightW - m.Px(56f), m.Px(64f)),
                    UiBattleCodex.ExpSources[i], UiTextRole.Body, shell.Size(23f),
                    TextAnchor.MiddleLeft, true);
            }

            var ruleTop = rowTop + boxH + m.Px(20f);
            var rule = shell.Panel("只加经验", m.Band(rightX, ruleTop, rightW, m.Px(180f)), shell.Size(28f));
            shell.LabelIn(rule.Root, new Rect(m.Px(28f), m.Px(28f), rightW - m.Px(56f), m.Px(120f)),
                UiBattleCodex.PartyRule + "。\n组队加成 " + UiBattleCodex.PartyBonusPct
                + "% 与人数口径写在 `config/exp.json`，界面只显示名目，不乘这一下。",
                UiTextRole.Body, shell.Size(23f), TextAnchor.UpperLeft, true);

            // ---------------------------------------------------------- 底：两个出口都没接口

            var footTop = ruleTop + m.Px(180f) + m.Px(28f);
            shell.ButtonIn(shell.Content, m.Band(rightX, footTop, (rightW - m.Px(20f)) / 2f, m.Px(96f)),
                "邀请", () => shell.Toast("小队邀请接口属 P2 社交，本卡只显示同场名单"),
                UiButtonKind.Normal);
            shell.ButtonIn(shell.Content,
                m.Band(rightX + (rightW - m.Px(20f)) / 2f + m.Px(20f), footTop, (rightW - m.Px(20f)) / 2f, m.Px(96f)),
                "离开小队", () => shell.Toast("离队接口属 P2 社交，这里不做"),
                UiButtonKind.Quiet);

            Debug.Log(ReadyLog + " members=" + shown + " party=" + flow.Party.Count);
            return shown;
        }

        /// <summary>血条：底一条、上面一条按比例。颜色只从色板来，这里不写色值。</summary>
        static void DrawBar(UiShell shell, Transform parent, Rect rect, float ratio)
        {
            var back = UiBuild.Box("HpBack", rect, parent);
            shell.Register(new UiTint(back, UiTextRole.Faint));

            var fillRect = new Rect(rect.x, rect.y, Mathf.Max(0f, rect.width * Mathf.Clamp01(ratio)), rect.height);
            var fill = UiBuild.Box("HpFill", fillRect, parent);
            shell.Register(new UiTint(fill, UiTextRole.Jade));
        }

        /// <summary>回包里的两个数直接相除，画条宽度用。这不是「算血」，血一直是服务端给的。</summary>
        static float Ratio(long value, long max)
        {
            return max <= 0 ? 0f : value / (float)max;
        }

        /// <summary>职业编号翻成屏上的称呼。取不到的照原样显示，不编一个职业出来。</summary>
        static string ClassName(string classId)
        {
            switch (classId)
            {
                case "tank":
                    return "坦克";
                case "warrior":
                    return "战士";
                case "mage":
                    return "法师";
                case "archer":
                    return "射手";
                case "priest":
                    return "牧师";
                default:
                    return string.IsNullOrEmpty(classId) ? "未知" : classId;
            }
        }
    }
}

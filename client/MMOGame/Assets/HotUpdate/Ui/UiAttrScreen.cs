using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 属性与加点（A8 / A9 的界面欠项）。四属性只读 `GET /character/attr-points` 回包里的
    /// **已加点数**，加点只 POST `/character/attr-points/allocate`（请求体只有属性键与点数，
    /// 事务号由网关生成）。
    ///
    /// 基础值只在职业表里，客户端不读表、不相加、不推算总值；
    /// 点数够不够、单属性上限、有没有免费洗点全在角色服判，界面照 `ok` / `reason` 写。
    /// 本版没有洗点口，这一屏也不给洗点按钮。
    /// </summary>
    public static class UiAttrScreen
    {
        public const string ReadyLog = "u5_attr_screen=ready";

        public static void Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell, UiUpgradeCodex.AttrsNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.46f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;

            var attr = flow.AttrPoints;
            var requested = flow.AttrPointsRequested;
            var shown = requested && attr.Ok;
            var allocated = flow.LastAllocate;

            // 左：四属性各已加多少点 + 手上还剩多少。全部照回包，没请求就写「尚未请求」。
            var current = shell.Panel(UiStrings.UiAttrScreen.T001,
                m.Band(pad, top + m.Px(56f), leftW, m.Px(620f)), shell.Size(34f));
            UiUpgradeViews.Rows(shell, current, UiUpgradeCodex.AttrNames,
                new[]
                {
                    shown ? attr.Strength + UiStrings.UiAttrScreen.T002 : UiStrings.UiAttrScreen.T003,
                    shown ? attr.Agility + UiStrings.UiAttrScreen.T002 : UiStrings.UiAttrScreen.T003,
                    shown ? attr.Spirit + UiStrings.UiAttrScreen.T002 : UiStrings.UiAttrScreen.T003,
                    shown ? attr.Vitality + UiStrings.UiAttrScreen.T002 : UiStrings.UiAttrScreen.T003,
                },
                m.Px(84f), m.Px(32f));
            UiUpgradeViews.Result(shell, current, m.Px(112f), UiStrings.UiAttrScreen.T004,
                shown ? attr.AttrPoints.ToString() : "—");

            // 右：每手加几点（+1 / +5 / 全部）+ 四条属性的加号。
            // 「全部」的数值取回包里的剩余点数——那是服务端事实值，不是我算出来的。
            var actions = shell.Panel(UiStrings.UiAttrScreen.T005,
                m.Band(rightX, top + m.Px(56f), rightW, m.Px(520f)), shell.Size(34f));

            var step = new[] { 1 };
            var steps = new[]
            {
                (UiStrings.UiAttrScreen.T006, 1),
                (UiStrings.UiAttrScreen.T007, 5),
                (UiStrings.UiAttrScreen.T008, -1),
            };
            for (var i = 0; i < steps.Length; i++)
            {
                var index = i;
                var opt = new UiOption(
                    actions.Root,
                    new Rect(m.Px(28f) + i * m.Px(200f), m.Px(36f), m.Px(188f), m.Px(64f)),
                    steps[i].Item1, steps[i].Item1, string.Empty,
                    UiOptionKind.Chip, shell.Size(24f), shell.Size(20f));
                opt.Selected = i == 0;
                opt.OnPick = picked =>
                {
                    step[0] = steps[index].Item2;
                    Debug.Log("u5_attr_step=" + (step[0] < 0 ? "all" : step[0].ToString()));
                };
                shell.Register(opt);
            }

            var keys = UiUpgradeCodex.Attrs;
            for (var i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                var col = i % 2;
                var row = i / 2;
                var rect = new Rect(
                    m.Px(28f) + col * (rightW - m.Px(56f)) * 0.5f,
                    m.Px(140f) + row * m.Px(112f),
                    (rightW - m.Px(56f)) * 0.5f - m.Px(12f),
                    m.Px(92f));
                shell.ButtonIn(actions.Root, rect,
                    UiUpgradeCodex.AttrNames[i] + UiStrings.UiAttrScreen.T009,
                    () =>
                    {
                        if (flow.Busy)
                        {
                            return;
                        }

                        // 点数是「这一手加几点」，够不够由角色服判；界面不先减一遍。
                        var points = step[0] < 0 ? attr.AttrPoints : step[0];
                        if (points < 1)
                        {
                            points = 1;
                        }

                        flow.AllocateAttr(key, points, shell.RefreshPage);
                    }, UiButtonKind.Normal);
            }

            shell.ButtonIn(actions.Root,
                new Rect(m.Px(28f), m.Px(376f), m.Px(240f), m.Px(76f)),
                UiStrings.UiAttrScreen.T010,
                () =>
                {
                    if (flow.Busy)
                    {
                        return;
                    }

                    flow.LoadAttrPoints(shell.RefreshPage);
                }, UiButtonKind.Primary);

            // 右下：加点回包。被拒时拒绝码原样带出，四属性照回包重画。
            var result = shell.Panel(UiStrings.UiAttrScreen.T011,
                m.Band(rightX, top + m.Px(56f) + m.Px(600f), rightW, m.Px(440f)), shell.Size(34f));
            UiUpgradeViews.Result(shell, result, m.Px(300f), UiStrings.UiAttrScreen.T012,
                flow.HasAllocate ? allocated.DisplayText : UiStrings.UiAttrScreen.T013);
            UiUpgradeViews.Result(shell, result, m.Px(244f), UiStrings.UiAttrScreen.T004,
                flow.HasAllocate && allocated.Ok ? allocated.AttrPoints.ToString() : "—");
            UiUpgradeViews.Result(shell, result, m.Px(188f), UiStrings.UiAttrScreen.T014,
                flow.HasAllocate && allocated.Duplicate ? UiStrings.UiAttrScreen.T015 : "—");

            Debug.Log(ReadyLog + " requested=" + (requested ? "true" : "false")
                + " shown=" + (shown ? "true" : "false")
                + " attr_points=" + (shown ? attr.AttrPoints.ToString() : "none")
                + " strength=" + attr.Strength
                + " agility=" + attr.Agility
                + " spirit=" + attr.Spirit
                + " vitality=" + attr.Vitality
                + " allocate=" + (flow.HasAllocate ? (allocated.Ok ? "ok" : "reject") : "none"));
        }
    }
}

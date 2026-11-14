using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 领地城战（原型 F02）。左边讲三层战力、领地收益与世界 Boss 归属，
    /// 右边是城战同屏规格与世界 Boss 入口的只读事实值。
    ///
    /// 同屏档位只按战斗服下发的 `SiegeView` 翻译：完整的画完整，超限的只留
    /// 队伍颜色和血条。归属只读 `/world/worldboss` 的 `owner_sect_name`，
    /// 没有名字就写「无人记名」，不编一个宗门上去。
    /// </summary>
    public static class UiSiegeScreen
    {
        public const string ReadyLog = "u7_siege_screen=ready";

        public static void Build(UiShell shell, UiSocialFlow flow)
        {
            UiUpgradeViews.Note(shell, UiSocialCodex.SiegeRule);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(16f);
            var leftW = (m.Width - pad * 2f - m.Px(40f)) * 0.5f;
            var rightX = pad + leftW + m.Px(40f);
            var rightW = m.Width - pad - rightX;

            // ---------------------------------------------------------- 左：三层与领地

            var layersH = m.Px(84f) + UiSocialCodex.Layers.Length * m.Px(76f) + m.Px(16f);
            var layers = shell.Panel("战力三层，互不污染", m.Band(pad, top + m.Px(56f), leftW, layersH),
                shell.Size(30f));
            for (var i = 0; i < UiSocialCodex.Layers.Length; i++)
            {
                var row = UiSocialCodex.Layers[i];
                var y = layersH - m.Px(84f) - m.Px(70f) - i * m.Px(76f);
                shell.LabelIn(layers.Root, new Rect(m.Px(28f), y + m.Px(30f), leftW - m.Px(56f), m.Px(34f)),
                    row[0], UiTextRole.Body, shell.Size(24f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(layers.Root, new Rect(m.Px(28f), y, leftW - m.Px(56f), m.Px(30f)),
                    row[1], UiTextRole.Muted, shell.Size(21f), TextAnchor.MiddleLeft, false);
            }

            var territoryTop = top + m.Px(56f) + layersH + gap;
            var territory = shell.Panel("领地收益", m.Band(pad, territoryTop, leftW, m.Px(200f)),
                shell.Size(30f));
            shell.LabelIn(territory.Root, new Rect(m.Px(28f), m.Px(28f), leftW - m.Px(56f), m.Px(120f)),
                UiSocialCodex.TerritoryRule, UiTextRole.Body, shell.Size(23f), TextAnchor.UpperLeft, true);

            var bossTop = territoryTop + m.Px(200f) + gap;
            var boss = shell.Panel("世界 Boss 归属", m.Band(pad, bossTop, leftW, m.Px(400f)), shell.Size(30f));
            var facts = flow.LastBoss;
            var hasBoss = flow.HasBoss;

            UiUpgradeViews.Result(shell, boss, m.Px(296f), "入口",
                hasBoss ? (facts.EntranceName.Length == 0 ? "未定" : facts.EntranceName) : "尚未读");
            UiUpgradeViews.Result(shell, boss, m.Px(248f), "这一刻",
                hasBoss ? (facts.Visible ? (facts.Killed ? "这一场已打掉" : "开着") : "没开") : "—");
            UiUpgradeViews.Result(shell, boss, m.Px(200f), "上限",
                hasBoss ? facts.Cap.ToString() : "—");
            UiUpgradeViews.Result(shell, boss, m.Px(152f), "记在入口上的名字",
                hasBoss ? Mmo.Net.WorldBossEntry.OwnerLine(facts) : "—");

            shell.LabelIn(boss.Root, new Rect(m.Px(28f), m.Px(96f), leftW - m.Px(56f), m.Px(50f)),
                UiSocialCodex.OwnerRule, UiTextRole.Faint, shell.Size(20f), TextAnchor.UpperLeft, true);

            shell.ButtonIn(boss.Root, new Rect(m.Px(28f), m.Px(24f), m.Px(300f), m.Px(64f)),
                "读一次入口",
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.LoadBoss(shell.RefreshPage);
                    }
                }, UiButtonKind.Normal);

            // ---------------------------------------------------------- 右：城战同屏

            var viewH = m.Px(84f) + m.Px(700f);
            var view = shell.Panel("城战同屏规格", m.Band(rightX, top + m.Px(56f), rightW, viewH),
                shell.Size(30f));

            UiUpgradeViews.Result(shell, view, m.Px(636f), "完整模型",
                flow.HasSiege ? flow.SiegeFullCount + " / 上限 " + flow.SiegeFullLimit : "尚未收到档位");
            UiUpgradeViews.Result(shell, view, m.Px(588f), "同屏单位",
                flow.HasSiege ? flow.SiegeUnitCount.ToString() : "—");
            UiUpgradeViews.Result(shell, view, m.Px(540f), "超限单位",
                flow.HasSiege ? flow.Markers().Count + " 个 · 只留队伍颜色和血条" : "—");

            var markers = flow.HasSiege ? flow.Markers() : new System.Collections.Generic.List<SiegeRow>();
            var rows = Mathf.Min(markers.Count, 6);
            for (var i = 0; i < rows; i++)
            {
                var row = markers[i];
                var y = m.Px(468f) - i * m.Px(62f);
                shell.LabelIn(view.Root, new Rect(m.Px(28f), y, rightW - m.Px(220f), m.Px(52f)),
                    "角色 " + row.MemberId + " · 队 " + row.Team + " · 血 " + row.Hp + " / " + row.HpMax,
                    UiTextRole.Body, shell.Size(23f), TextAnchor.MiddleLeft, false);
                shell.Chip(view.Root,
                    new Rect(rightW - m.Px(28f) - m.Px(150f), y + m.Px(6f), m.Px(150f), m.Px(40f)),
                    "只留队色", UiChipKind.Mute);
            }

            var fullCount = flow.HasSiege ? flow.SiegeRows.Count - markers.Count : 0;
            shell.LabelIn(view.Root, new Rect(m.Px(28f), m.Px(96f), rightW - m.Px(56f), m.Px(150f)),
                flow.HasSiege
                    ? "这一场有 " + fullCount + " 个画完整、剩下的只留队色与血条；"
                        + "档位是战斗服按上限算的，界面不按距离重排。"
                    : "还没收到这一场的档位：城战实例才会发这条消息，副本 / 竞技场 / 战场都没有。",
                UiTextRole.Muted, shell.Size(22f), TextAnchor.UpperLeft, true);

            shell.LabelIn(view.Root, new Rect(m.Px(28f), m.Px(24f), rightW - m.Px(56f), m.Px(60f)),
                "超限没有完整模型是结构保证：消息里没有模型、坐标与档位以外的字段。",
                UiTextRole.Warm, shell.Size(20f), TextAnchor.MiddleLeft, false);

            Debug.Log(ReadyLog + " layers=" + UiSocialCodex.Layers.Length
                + " markers=" + markers.Count
                + " has_siege=" + (flow.HasSiege ? "true" : "false")
                + " has_boss=" + (flow.HasBoss ? "true" : "false"));
        }
    }
}

using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 角色栏（原型 A04，方案 12.1「第二角色栏」）。
    ///
    /// 第一行是服务端刚确认过的那位修士——名字、职业、等级全部来自建角回包，
    /// 界面不自己算；第二格写「未开启」，因为第二角色栏是商城便利项（P1-5c 之后），
    /// 本卡没有发货、也就没有第二个角色可列，这里不假装有。
    ///
    /// 「进入所选角色」只在有真实角色时可按；按下去只是去载入屏，
    /// 能不能进世界仍以服务端回包为准。
    /// </summary>
    public static class UiRosterScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u2_roster=ready";

        /// <summary>共享与不共享四行。写定规则，不做成「外观跟着角色走」。</summary>
        static readonly string[][] Sharing =
        {
            new[] { "外观收藏", "可以共享" },
            new[] { "装备", "不共享" },
            new[] { "等级", "不共享" },
            new[] { "境界", "不共享" },
        };

        /// <summary>建屏。</summary>
        public static void Build(UiShell shell, UiEntryFlow flow)
        {
            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(190f);
            var gap = m.Px(60f);
            var colW = (m.Width - pad * 2f - gap) / 2f;
            var leftX = pad;
            var rightX = pad + colW + gap;

            var character = flow.Character;
            var roleName = RoleName(character.ClassId);
            var characterName = string.IsNullOrEmpty(character.Name) ? "无名" : character.Name;

            // ---------------------------------------------------------- 左：角色行

            var rowH = m.Px(148f);
            var current = shell.Panel(null, m.Band(leftX, top, colW, rowH), shell.Size(28f));
            shell.LabelIn(current.Root, new Rect(m.Px(28f), rowH - m.Px(66f), colW - m.Px(200f), m.Px(46f)),
                characterName + " · " + roleName, UiTextRole.Title, shell.Size(32f), TextAnchor.MiddleLeft, false);
            shell.LabelIn(current.Root, new Rect(m.Px(28f), m.Px(26f), colW - m.Px(220f), m.Px(44f)),
                "等级 " + character.Level + " · 当前角色", UiTextRole.Muted, shell.Size(24f),
                TextAnchor.MiddleLeft, false);
            shell.Chip(current.Root, new Rect(colW - m.Px(28f) - m.Px(120f), rowH - m.Px(64f), m.Px(120f), m.Px(44f)),
                "在世", UiChipKind.Success);

            var secondTop = top + rowH + m.Px(20f);
            var second = shell.Panel(null, m.Band(leftX, secondTop, colW, rowH), shell.Size(28f));
            shell.LabelIn(second.Root, new Rect(m.Px(28f), rowH - m.Px(66f), colW - m.Px(200f), m.Px(46f)),
                "第二修士", UiTextRole.Muted, shell.Size(32f), TextAnchor.MiddleLeft, false);
            shell.LabelIn(second.Root, new Rect(m.Px(28f), m.Px(26f), colW - m.Px(220f), m.Px(44f)),
                "尚未开启。装备、等级、境界不共享", UiTextRole.Faint, shell.Size(22f),
                TextAnchor.MiddleLeft, false);
            shell.Chip(second.Root, new Rect(colW - m.Px(28f) - m.Px(120f), rowH - m.Px(64f), m.Px(120f), m.Px(44f)),
                "未开启", UiChipKind.Mute);

            var shareTop = secondTop + rowH + m.Px(28f);
            var shareH = m.Px(104f) + Sharing.Length * m.Px(46f);
            var share = shell.Panel("共享与不共享", m.Band(leftX, shareTop, colW, shareH), shell.Size(30f));
            var y = shareH - m.Px(84f);
            for (var i = 0; i < Sharing.Length; i++)
            {
                y -= m.Px(46f);
                shell.LabelIn(share.Root, new Rect(m.Px(28f), y, colW * 0.46f, m.Px(46f)), Sharing[i][0],
                    UiTextRole.Body, shell.Size(24f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(share.Root, new Rect(m.Px(28f) + colW * 0.46f, y, colW * 0.5f, m.Px(46f)),
                    Sharing[i][1], UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);
            }

            // ---------------------------------------------------------- 右：开启方式与选择

            var openH = m.Px(104f) + m.Px(120f);
            var open = shell.Panel("开启方式", m.Band(rightX, top, colW, openH), shell.Size(30f));
            shell.LabelIn(open.Root, new Rect(m.Px(28f), m.Px(30f), colW - m.Px(56f), m.Px(110f)),
                "商城里的「第二角色栏」是便利项，只加一个角色位；不加属性、不加副本次数、不抬高任何上限。",
                UiTextRole.Body, shell.Size(26f), TextAnchor.UpperLeft, true);

            var pickTop = top + openH + m.Px(28f);
            var pickH = m.Px(104f) + m.Px(120f);
            var pick = shell.Panel("登录后的角色选择", m.Band(rightX, pickTop, colW, pickH), shell.Size(30f));
            shell.LabelIn(pick.Root, new Rect(m.Px(28f), m.Px(30f), colW - m.Px(56f), m.Px(110f)),
                "同账号已有两名修士时，在此选择进入哪一位。这是唯一需要多出的入口，不额外做切换动画或角色预览。",
                UiTextRole.Body, shell.Size(26f), TextAnchor.UpperLeft, true);

            shell.Label(m.Band(rightX, pickTop + pickH + m.Px(24f), colW, m.Px(48f)),
                "边界：外观收藏共享是写定规则，别做成「外观也跟着角色走」。",
                UiTextRole.Warm, shell.Size(22f), TextAnchor.MiddleLeft, false);

            // ---------------------------------------------------------- 底部

            var enter = shell.Button(new Rect(m.Width - pad - m.Px(360f), m.Px(120f), m.Px(360f), m.Px(84f)),
                "进入所选角色", flow.EnterChosen, UiButtonKind.Primary);
            flow.BindRoster(enter);

            Debug.Log(ReadyLog + " rows=2 open=1 id=" + character.CharacterId
                + " class=" + character.ClassId + " level=" + character.Level);
        }

        /// <summary>职业编号翻成名字。翻不出来就把编号原样显示，不猜。</summary>
        static string RoleName(string classId)
        {
            var role = UiEntryCodex.FindRole(classId);
            return role == null ? (string.IsNullOrEmpty(classId) ? "未知职责" : classId) : role.Value.Name;
        }
    }
}

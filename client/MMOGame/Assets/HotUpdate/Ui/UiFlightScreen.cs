using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 飞行与境界锁（原型 B05）。
    ///
    /// 飞行只由境界打开，不由装备、坐骑或商城拖尾打开。
    /// 请求只带 `want_fly`；能不能飞由世界服问角色服的境界后判，
    /// 被拒时回包里的 y 是拉回后的地面高度，屏上把「画面高度」改成那个数，
    /// 不保留本地起飞前攒的高度，也不让拖尾亮起来。
    /// </summary>
    public static class UiFlightScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u4_flight_screen=ready";

        /// <summary>建屏。返回列出来的地表条数，自检拿它核对。</summary>
        public static int Build(UiShell shell, UiWorldFlow flow)
        {
            var m = shell.Metrics;
            var pad = m.Px(90f);
            var gap = m.Px(40f);
            var leftW = (m.Width - pad * 2f - gap) * 0.46f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;
            flow.ClearViews();

            shell.Label(m.Band(pad, m.Px(80f), m.Width - pad * 2f, m.Px(60f)),
                "飞行与境界锁", UiTextRole.Title, shell.Size(50f), TextAnchor.MiddleLeft, false);
            shell.Label(m.Band(pad, m.Px(146f), leftW * 1.4f, m.Px(44f)),
                "飞行只由境界打开，不由装备、坐骑或商城拖尾打开",
                UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, true);

            // ---------------------------------------------------------- 左：三段境界

            var realmTop = m.Px(220f);
            var realmH = m.Px(320f);
            var realm = shell.Panel("三段大境界（首发）", m.Band(pad, realmTop, leftW, realmH), shell.Size(28f));
            for (var i = 0; i < UiWorldCodex.Realms.Length; i++)
            {
                shell.Chip(realm.Root,
                    new Rect(m.Px(26f) + i * m.Px(200f), realmH - m.Px(140f), m.Px(180f), m.Px(64f)),
                    UiWorldCodex.Realms[i],
                    i == 1 ? UiChipKind.Success : UiChipKind.Mute);
            }

            shell.LabelIn(realm.Root, new Rect(m.Px(26f), m.Px(60f), leftW - m.Px(52f), m.Px(100f)),
                "config/flight.json 的 flight_realm 写的是 " + UiWorldCodex.FlightRealm
                + "（待定）；本屏不读配置判权限，能不能飞只看回包的 can_fly。",
                UiTextRole.Faint, shell.Size(22f), TextAnchor.UpperLeft, true);

            var askTop = realmTop + realmH + m.Px(20f);
            var askH = m.Px(260f);
            var ask = shell.Panel("请求字段", m.Band(pad, askTop, leftW, askH), shell.Size(26f));
            shell.LabelIn(ask.Root, new Rect(m.Px(26f), askH - m.Px(150f), leftW - m.Px(52f), m.Px(110f)),
                "want_fly。伪造 realm / can_fly / account / y 一律无效；"
                + "位置帧直接报高 y 会被 flight_forbidden 拉回 GroundY。",
                UiTextRole.Body, shell.Size(23f), TextAnchor.UpperLeft, true);

            var upY = m.Px(60f);
            var upBtn = shell.ButtonIn(ask.Root,
                new Rect(m.Px(26f), upY, m.Px(240f), m.Px(88f)),
                "起飞", () => flow.Fly(true), UiButtonKind.Primary);
            var downBtn = shell.ButtonIn(ask.Root,
                new Rect(m.Px(286f), upY, m.Px(240f), m.Px(88f)),
                "落地", () => flow.Fly(false), UiButtonKind.Quiet);

            // ---------------------------------------------------------- 右：地表境界要求

            var listTop = m.Px(220f);
            var listH = m.Px(560f);
            var list = shell.Panel("地表境界要求", m.Band(rightX, listTop, rightW, listH), shell.Size(28f));
            for (var i = 0; i < UiWorldCodex.Regions.Length; i++)
            {
                var region = UiWorldCodex.Regions[i];
                shell.LabelIn(list.Root,
                    new Rect(m.Px(28f), listH - m.Px(140f) - i * m.Px(52f), rightW - m.Px(56f), m.Px(48f)),
                    region.Name + "    " + region.Realm + (region.Open ? "" : "    · 剧情未开"),
                    region.Open ? UiTextRole.Body : UiTextRole.Faint,
                    shell.Size(23f), TextAnchor.MiddleLeft, false);
            }

            var trailTop = listTop + listH + m.Px(20f);
            var trail = shell.Panel("拖尾", m.Band(rightX, trailTop, rightW, m.Px(220f)), shell.Size(26f));
            shell.LabelIn(trail.Root, new Rect(m.Px(28f), m.Px(60f), rightW - m.Px(56f), m.Px(120f)),
                "TrailAllowed 只在 Flying 为真时成立；没有飞行权限时，买了拖尾也不会让人飞。",
                UiTextRole.Body, shell.Size(23f), TextAnchor.UpperLeft, true);

            // ---------------------------------------------------------- 底：回包两行

            var lineTop = trailTop + m.Px(220f) + m.Px(18f);
            var line = shell.Label(m.Band(rightX, lineTop, rightW, m.Px(52f)),
                UiWorldCodex.NoFlyYet, UiTextRole.Muted, shell.Size(25f), TextAnchor.MiddleLeft, true);
            var yline = shell.Label(m.Band(rightX, lineTop + m.Px(56f), rightW, m.Px(52f)),
                "画面高度以回包为准 · 拖尾只在能飞时开", UiTextRole.Faint, shell.Size(23f),
                TextAnchor.MiddleLeft, true);

            flow.RegisterView(() =>
            {
                line.Label.text = flow.FlyLine();
                yline.Label.text = flow.YLine();
                upBtn.Enabled = !flow.Busy;
                downBtn.Enabled = !flow.Busy;
            });

            Debug.Log(ReadyLog + " realms=" + UiWorldCodex.Realms.Length
                + " flight_realm=" + UiWorldCodex.FlightRealm
                + " regions=" + UiWorldCodex.Regions.Length);

            return UiWorldCodex.Regions.Length;
        }
    }
}

using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U17 世界 Boss 入口屏。
    ///
    /// 只读 `GET /world/worldboss`（`WorldBossEntry.WorldBossFacts`）：`visible` / `killed`
    /// 决定开没开、打没打掉，`current` / `cap` / `remaining` 写「已进 3 / 20」，
    /// `hold_s` / `renew_s` / `lost_s` 只作说明文案。窗口时刻不在客户端折时区。
    /// </summary>
    public static class UiWorldBossScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u17_world_boss=ready";

        public const string Notice = "入口 / 名额 / 三个时长都读世界服回包 · 客户端不算名额、不折时区";

        /// <summary>还没读过入口时的文案。</summary>
        public const string NotAsked = "入口还没读 · 点「读取入口」问世界服";

        public static int Build(UiShell shell, UiSocialFlow flow)
        {
            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.54f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;
            var rowTop = top + m.Px(64f);

            shell.Label(new Rect(pad, m.Height - top - m.Px(40f), m.Width - pad * 2f, m.Px(40f)),
                Notice, UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);

            var facts = flow.LastBoss;
            var has = flow.HasBoss;

            // ---------------------------------------------------------- 左：入口与名额

            var headH = m.Px(340f);
            var head = shell.Panel("入口", m.Band(pad, rowTop, leftW, headH), shell.Size(30f));
            var title = has
                ? (facts.EntranceName.Length == 0 ? "入口未定" : facts.EntranceName)
                : NotAsked;
            shell.LabelIn(head.Root, new Rect(m.Px(28f), headH - m.Px(120f), leftW - m.Px(56f), m.Px(72f)),
                title, UiTextRole.Title, shell.Size(38f), TextAnchor.MiddleLeft, true);

            var state = !has
                ? "尚未读取"
                : facts.Visible
                    ? (facts.Killed ? "这一场已打掉" : "这一场还开着")
                    : "这一刻没开：" + (facts.Reason.Length == 0 ? "原因没给" : facts.Reason);
            shell.Chip(head.Root, new Rect(m.Px(28f), m.Px(150f), m.Px(300f), m.Px(46f)),
                state, !has ? UiChipKind.Mute : (facts.Visible && !facts.Killed ? UiChipKind.Success : UiChipKind.Error));

            shell.LabelIn(head.Root, new Rect(m.Px(28f), m.Px(84f), leftW - m.Px(56f), m.Px(56f)),
                has && facts.Cap > 0
                    ? "已进 " + facts.Current + " / " + facts.Cap + " · 还能进 " + facts.Remaining
                    : "名额看回包",
                UiTextRole.Body, shell.Size(26f), TextAnchor.MiddleLeft, false);

            // ---------------------------------------------------------- 右：三个时长与归属

            var timeTop = rowTop;
            var timeH = m.Px(240f);
            var time = shell.Panel("三个时长（只作说明）", m.Band(rightX, timeTop, rightW, timeH), shell.Size(30f));
            shell.LabelIn(time.Root, new Rect(m.Px(28f), m.Px(28f), rightW - m.Px(56f), timeH - m.Px(96f)),
                has
                    ? "记名后保留 " + facts.HoldSeconds + " 秒 · 续期 " + facts.RenewSeconds
                        + " 秒 · 掉了 " + facts.LostSeconds + " 秒算丢"
                    : "还没读到入口",
                UiTextRole.Body, shell.Size(24f), TextAnchor.UpperLeft, true);

            var ownerTop = timeTop + timeH + m.Px(20f);
            var owner = shell.Panel("归属", m.Band(rightX, ownerTop, rightW, m.Px(160f)), shell.Size(30f));
            shell.LabelIn(owner.Root, new Rect(m.Px(28f), m.Px(28f), rightW - m.Px(56f), m.Px(104f)),
                has ? WorldBossEntry.OwnerLine(facts) : "还没读到入口",
                UiTextRole.Body, shell.Size(26f), TextAnchor.MiddleLeft, true);

            shell.ButtonIn(shell.Content, m.Band(rightX, ownerTop + m.Px(180f), rightW, m.Px(96f)),
                "读取入口", () => flow.LoadBoss(shell.RefreshPage), UiButtonKind.Primary);

            // ---------------------------------------------------------- 底：口径

            var footTop = rowTop + headH + m.Px(24f);
            shell.Label(new Rect(pad, m.Height - footTop - m.Px(96f), m.Width - pad * 2f, m.Px(96f)),
                "窗口时刻由世界服与 `config/calendar.json` 持有，界面照回包写，不按本地时钟判开没开。",
                UiTextRole.Faint, shell.Size(22f), TextAnchor.UpperLeft, true);

            Debug.Log(ReadyLog + " has=" + (has ? "true" : "false")
                + " visible=" + (has && facts.Visible ? "true" : "false")
                + " killed=" + (has && facts.Killed ? "true" : "false")
                + " current=" + facts.Current + "/" + facts.Cap
                + " remaining=" + facts.Remaining
                + " state=" + state);
            return has ? facts.Current : 0;
        }
    }
}

using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 境界突破（原型 D07）。当前境界只读 `/inventory` 回包里的 realm；
    /// 突破接口在后面的卡，这一屏不提供按钮，也不本地推境界。
    /// </summary>
    public static class UiRealmScreen
    {
        public const string ReadyLog = "u5_realm_screen=ready";

        public static int Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell, UiUpgradeCodex.RealmNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.42f;
            var cardH = m.Px(520f);

            var current = shell.Panel("当前", m.Band(pad, top + m.Px(56f), leftW, cardH), shell.Size(34f));
            var realm = flow.HasBag ? flow.Bag.Realm : "";
            UiUpgradeViews.Rows(shell, current,
                new[] { "境界", "等级", "突破" },
                new[]
                {
                    realm.Length > 0 ? realm : "尚未读到",
                    "以角色服为准",
                    "后面的卡",
                },
                m.Px(72f), m.Px(32f));

            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;
            var ladder = shell.Panel("境界阶", m.Band(rightX, top + m.Px(56f), rightW, m.Px(760f)), shell.Size(34f));
            var labels = new string[UiUpgradeCodex.Realms.Length];
            var values = new string[UiUpgradeCodex.Realms.Length];
            for (var i = 0; i < UiUpgradeCodex.Realms.Length; i++)
            {
                labels[i] = "第 " + (i + 1) + " 阶";
                values[i] = UiUpgradeCodex.Realms[i];
            }

            UiUpgradeViews.Rows(shell, ladder, labels, values, m.Px(58f), m.Px(28f));

            Debug.Log(ReadyLog + " realm=" + realm + " steps=" + UiUpgradeCodex.Realms.Length);
            return UiUpgradeCodex.Realms.Length;
        }
    }
}

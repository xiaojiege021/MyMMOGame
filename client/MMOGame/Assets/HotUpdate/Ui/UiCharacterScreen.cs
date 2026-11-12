using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 角色与装备（原型 D01）。名号、境界、背包容量都来自 `/inventory` 回包；
    /// 四个部位只是位置示意，装备与部位归属以服务端背包为准，界面不本地判。
    /// </summary>
    public static class UiCharacterScreen
    {
        public const string ReadyLog = "u5_character=ready";

        public static int Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell,
                "角色与装备 · 名号 / 境界 / 背包来自 /inventory 回包；部位与装备以服务端为准");

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.52f;
            var cardH = m.Px(520f);
            var card = shell.Panel("角色", m.Band(pad, top + m.Px(56f), leftW, cardH), shell.Size(36f));

            var name = flow.HasBag ? flow.Bag.CharacterName : "";
            var realm = flow.HasBag ? flow.Bag.Realm : "";
            UiUpgradeViews.Rows(shell, card,
                new[] { "名号", "境界", "背包", "角色号" },
                new[]
                {
                    name.Length > 0 ? name : "尚未读到",
                    realm.Length > 0 ? realm : "尚未读到",
                    flow.HasBag
                        ? (flow.Bag.Slots != null ? flow.Bag.Slots.Count : 0) + " / " + flow.Bag.BagSlots + " 格"
                        : "尚未读背包",
                    flow.CharacterId != 0 ? flow.CharacterId.ToString() : "未绑定",
                },
                m.Px(64f), m.Px(36f));

            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;
            var cellW = (rightW - gap) * 0.5f;
            var cellH = m.Px(220f);
            for (var i = 0; i < UiUpgradeCodex.Parts.Length; i++)
            {
                var col = i % 2;
                var row = i / 2;
                var rect = m.Band(rightX + col * (cellW + gap), top + m.Px(56f) + row * (cellH + gap), cellW, cellH);
                var part = shell.Panel(UiUpgradeCodex.Parts[i], rect, shell.Size(30f));
                shell.LabelIn(part.Root,
                    new Rect(m.Px(28f), m.Px(40f), cellW - m.Px(56f), m.Px(80f)),
                    "装备与部位以服务端背包为准", UiTextRole.Faint, shell.Size(22f),
                    TextAnchor.MiddleLeft, true);
            }

            Debug.Log(ReadyLog + " name=" + name + " realm=" + realm
                + " slots=" + (flow.HasBag && flow.Bag.Slots != null ? flow.Bag.Slots.Count : 0));
            return UiUpgradeCodex.Parts.Length;
        }
    }
}

using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 角色与装备（原型 D01）。名号、境界、背包容量都来自 `/inventory` 回包；
    /// 境界显示用 `realm_display`（立仙金仙 / 立魔散仙），规范名与世界服门槛仍只认 `realm`。
    /// 四个部位只是位置示意，装备与部位归属以服务端背包为准，界面不本地判。
    /// </summary>
    public static class UiCharacterScreen
    {
        public const string ReadyLog = "u5_character=ready";

        public static int Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell,
                UiStrings.UiCharacterScreen.T001);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.52f;
            var cardH = m.Px(520f);
            var card = shell.Panel(UiStrings.UiCharacterScreen.T002, m.Band(pad, top + m.Px(56f), leftW, cardH), shell.Size(36f));

            var name = flow.HasBag ? flow.Bag.CharacterName : "";
            var realm = flow.HasBag ? flow.Bag.DisplayRealm : "";
            UiUpgradeViews.Rows(shell, card,
                new[] { UiStrings.UiCharacterScreen.T003, UiStrings.UiCharacterScreen.T004, UiStrings.UiCharacterScreen.T005, UiStrings.UiCharacterScreen.T006 },
                new[]
                {
                    name.Length > 0 ? name : UiStrings.UiCharacterScreen.T007,
                    realm.Length > 0 ? realm : UiStrings.UiCharacterScreen.T007,
                    flow.HasBag
                        ? (flow.Bag.Slots != null ? flow.Bag.Slots.Count : 0) + " / " + flow.Bag.BagSlots + UiStrings.UiCharacterScreen.T008
                        : UiStrings.UiCharacterScreen.T009,
                    flow.CharacterId != 0 ? flow.CharacterId.ToString() : UiStrings.UiCharacterScreen.T010,
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
                    UiStrings.UiCharacterScreen.T011, UiTextRole.Faint, shell.Size(22f),
                    TextAnchor.MiddleLeft, true);
            }

            Debug.Log(ReadyLog + " name=" + name + " realm=" + realm
                + " slots=" + (flow.HasBag && flow.Bag.Slots != null ? flow.Bag.Slots.Count : 0));
            return UiUpgradeCodex.Parts.Length;
        }
    }
}

using Mmo.Net;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 家园洞府（原型 F04）。左边四段仍是方案口径；右边接 C3 的 `/homestead*`：
    /// 六格展示服务端摆放事实，摆 / 撤走网关，事务号由网关生成。
    /// 种植与家园币服务端尚未落地，如实标「尚未接服务端」，不编产量数字。
    /// </summary>
    public static class UiHomesteadScreen
    {
        public const string ReadyLog = "u7_homestead_screen=ready";

        public const string WiredLog = "u9_homestead_wired=ready";

        static string _pickedSlot = string.Empty;

        static string _pickedItem = string.Empty;

        public static void Build(UiShell shell, UiSocialFlow flow)
        {
            UiUpgradeViews.Note(shell, UiSocialCodex.HomesteadRule);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(16f);
            var leftW = (m.Width - pad * 2f - m.Px(40f)) * 0.42f;
            var rightX = pad + leftW + m.Px(40f);
            var rightW = m.Width - pad - rightX;

            // ---------------------------------------------------------- 左：四段口径

            var cardH = m.Px(168f);
            for (var i = 0; i < UiSocialCodex.Homestead.Length; i++)
            {
                var row = UiSocialCodex.Homestead[i];
                var y = top + m.Px(56f) + (UiSocialCodex.Homestead.Length - 1 - i) * (cardH + gap);
                var card = shell.Panel(row[0], m.Band(pad, y, leftW, cardH), shell.Size(28f));
                shell.LabelIn(card.Root, new Rect(m.Px(24f), m.Px(24f), leftW - m.Px(48f), m.Px(90f)),
                    row[1], UiTextRole.Muted, shell.Size(20f), TextAnchor.UpperLeft, true);
            }

            // ---------------------------------------------------------- 右：摆放事实

            var factsTop = top + m.Px(56f);
            var factsH = m.Px(520f);
            var facts = shell.Panel("洞府摆放", m.Band(rightX, factsTop, rightW, factsH), shell.Size(28f));

            if (!flow.HasHomestead)
            {
                shell.LabelIn(facts.Root,
                    new Rect(m.Px(24f), m.Px(360f), rightW - m.Px(48f), m.Px(80f)),
                    "尚未请求 · 点「读取洞府」走 /homestead",
                    UiTextRole.Muted, shell.Size(22f), TextAnchor.MiddleLeft, true);
                shell.ButtonIn(facts.Root,
                    new Rect(m.Px(24f), m.Px(48f), m.Px(280f), m.Px(72f)),
                    "读取洞府",
                    () =>
                    {
                        flow.LoadHomestead(0, () =>
                        {
                            flow.LoadHomesteadBag(() => shell.RefreshPage());
                        });
                    },
                    UiButtonKind.Primary);
            }
            else if (!flow.LastHomestead.Ok
                     && (flow.LastHomestead.Placements == null || flow.LastHomestead.Placements.Count == 0)
                     && !string.IsNullOrEmpty(flow.LastHomestead.Reason)
                     && flow.LastHomestead.Reason != "bad_reply"
                     && flow.LastHomestead.SlotCount == 0)
            {
                shell.LabelIn(facts.Root,
                    new Rect(m.Px(24f), m.Px(360f), rightW - m.Px(48f), m.Px(100f)),
                    "服务端：" + flow.LastHomestead.Reason,
                    UiTextRole.Warm, shell.Size(22f), TextAnchor.UpperLeft, true);
                shell.ButtonIn(facts.Root,
                    new Rect(m.Px(24f), m.Px(48f), m.Px(280f), m.Px(72f)),
                    "再读一次",
                    () => flow.LoadHomestead(0, () => shell.RefreshPage()),
                    UiButtonKind.Quiet);
            }
            else
            {
                DrawSlots(shell, flow, facts, rightW, factsH);
            }

            // ---------------------------------------------------------- 种植缺口 + 口径

            var plantTop = factsTop + factsH + gap;
            var plant = shell.Panel("种植与家园币", m.Band(rightX, plantTop, rightW, m.Px(160f)),
                shell.Size(28f));
            shell.LabelIn(plant.Root,
                new Rect(m.Px(24f), m.Px(40f), rightW - m.Px(48f), m.Px(80f)),
                UiSocialCodex.PlantingGap, UiTextRole.Warm, shell.Size(21f), TextAnchor.UpperLeft, true);

            var ruleTop = plantTop + m.Px(160f) + gap;
            var rule = shell.Panel("口径", m.Band(rightX, ruleTop, rightW, m.Px(200f)), shell.Size(28f));
            var rules = new[]
            {
                "摆件不消耗背包、不加属性、不进伤害公式",
                "家园币只换外观与便捷（接口未接，不编数字）",
                "访客可只读；摆 / 撤须是洞府主人",
            };
            for (var i = 0; i < rules.Length; i++)
            {
                shell.LabelIn(rule.Root,
                    new Rect(m.Px(24f), m.Px(120f) - i * m.Px(44f), rightW - m.Px(48f), m.Px(40f)),
                    rules[i], UiTextRole.Body, shell.Size(20f), TextAnchor.MiddleLeft, false);
            }

            Debug.Log(ReadyLog + " cards=" + UiSocialCodex.Homestead.Length
                + " requests=" + flow.Requests
                + " has=" + flow.HasHomestead);
            Debug.Log(WiredLog + " slots=" + UiSocialCodex.HomesteadSlots.Length
                + " decor=" + UiSocialCodex.DecorItemIds.Length);
        }

        static void DrawSlots(UiShell shell, UiSocialFlow flow, UiPanel facts, float rightW, float factsH)
        {
            var m = shell.Metrics;
            var outcome = flow.LastHomestead;
            shell.LabelIn(facts.Root,
                new Rect(m.Px(24f), factsH - m.Px(72f), rightW - m.Px(48f), m.Px(40f)),
                outcome.DisplayText
                + (outcome.Duplicate ? " · 重放" : string.Empty),
                UiTextRole.Muted, shell.Size(20f), TextAnchor.MiddleLeft, false);

            var rowH = m.Px(56f);
            var y = factsH - m.Px(120f);
            for (var i = 0; i < UiSocialCodex.HomesteadSlots.Length; i++)
            {
                var slot = UiSocialCodex.HomesteadSlots[i];
                var item = HomesteadEntry.ItemInSlot(outcome, slot);
                var line = item.Length == 0 ? slot + " · 空" : slot + " · " + item;
                shell.LabelIn(facts.Root,
                    new Rect(m.Px(24f), y - i * rowH, rightW * 0.42f, rowH - m.Px(8f)),
                    line, UiTextRole.Body, shell.Size(20f), TextAnchor.MiddleLeft, false);

                var slotCopy = slot;
                if (item.Length == 0)
                {
                    shell.ButtonIn(facts.Root,
                        new Rect(rightW * 0.48f, y - i * rowH, m.Px(120f), rowH - m.Px(12f)),
                        "选此格",
                        () =>
                        {
                            _pickedSlot = slotCopy;
                            shell.RefreshPage();
                        },
                        _pickedSlot == slot ? UiButtonKind.Primary : UiButtonKind.Quiet);
                }
                else
                {
                    shell.ButtonIn(facts.Root,
                        new Rect(rightW * 0.48f, y - i * rowH, m.Px(120f), rowH - m.Px(12f)),
                        "撤下",
                        () =>
                        {
                            flow.RemoveDecor(slotCopy, () => shell.RefreshPage());
                        },
                        UiButtonKind.Quiet);
                }
            }

            var decor = flow.DecorInBag();
            var decorTop = m.Px(48f);
            shell.LabelIn(facts.Root,
                new Rect(m.Px(24f), decorTop + m.Px(100f), rightW - m.Px(48f), m.Px(36f)),
                decor.Count == 0
                    ? (flow.HasHomesteadBag ? "背包里没有可摆摆件" : "尚未读背包")
                    : "背包摆件 · 点选后摆到所选格",
                UiTextRole.Faint, shell.Size(18f), TextAnchor.MiddleLeft, false);

            for (var i = 0; i < decor.Count && i < 3; i++)
            {
                var id = decor[i];
                shell.ButtonIn(facts.Root,
                    new Rect(m.Px(24f) + i * m.Px(160f), decorTop + m.Px(28f), m.Px(148f), m.Px(56f)),
                    id,
                    () =>
                    {
                        _pickedItem = id;
                        shell.RefreshPage();
                    },
                    _pickedItem == id ? UiButtonKind.Primary : UiButtonKind.Quiet);
            }

            var canPlace = _pickedSlot.Length > 0 && _pickedItem.Length > 0;
            shell.ButtonIn(facts.Root,
                new Rect(rightW - m.Px(200f), decorTop + m.Px(28f), m.Px(176f), m.Px(56f)),
                canPlace ? "摆上" : "先选格与件",
                () =>
                {
                    if (!canPlace || flow.Busy)
                    {
                        return;
                    }

                    var slot = _pickedSlot;
                    var item = _pickedItem;
                    flow.PlaceDecor(slot, item, () =>
                    {
                        _pickedSlot = string.Empty;
                        _pickedItem = string.Empty;
                        shell.RefreshPage();
                    });
                },
                canPlace ? UiButtonKind.Primary : UiButtonKind.Quiet);
        }
    }
}

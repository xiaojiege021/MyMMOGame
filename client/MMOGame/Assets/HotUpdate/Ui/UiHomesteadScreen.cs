using System;
using Mmo.Net;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 家园洞府（原型 F04）。左四段是方案口径；右三块都接服务端：
    /// 六格摆放走 C3 的 `/homestead*`，种植与家园币走 N11 的 `/homestead/plants`、
    /// `/homestead/plant|harvest` 与 `/home-coin/shop/buy`。
    ///
    /// 格位熟没熟、这次收了多少、扣了多少、余额多少，全部照抄回包；产量、成长时长、
    /// 标价都在服务端配置里，这里不读、不算、不编。
    /// </summary>
    public static class UiHomesteadScreen
    {
        public const string ReadyLog = "u7_homestead_screen=ready";

        public const string WiredLog = "u9_homestead_wired=ready";

        public const string PlantLog = "u10_homestead_plant=ready";

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

            // ---------------------------------------------------------- 右：种植

            var plantTop = factsTop + factsH + gap;
            var plantH = m.Px(320f);
            var plant = shell.Panel("种植", m.Band(rightX, plantTop, rightW, plantH), shell.Size(28f));
            DrawPlants(shell, flow, plant, rightW, plantH);

            // ---------------------------------------------------------- 右：家园币

            var coinTop = plantTop + plantH + gap;
            var coinH = m.Px(260f);
            var coin = shell.Panel("家园币", m.Band(rightX, coinTop, rightW, coinH), shell.Size(28f));
            DrawHomeCoin(shell, flow, coin, rightW);

            // ---------------------------------------------------------- 右：口径

            var ruleTop = coinTop + coinH + gap;
            var rule = shell.Panel("口径", m.Band(rightX, ruleTop, rightW, m.Px(200f)), shell.Size(28f));
            var rules = new[]
            {
                "摆件不消耗背包、不加属性、不进伤害公式",
                "家园币只换外观与便捷：不换金币、材料、副本次数或战力",
                "访客可只读；种 / 收 / 摆 / 撤须是洞府主人",
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
            Debug.Log(PlantLog + " plant_slots=" + UiSocialCodex.PlantSlots.Length
                + " crops=" + UiSocialCodex.CropIds.Length
                + " shop=" + UiSocialCodex.HomeCoinShopItemIds.Length
                + " has_plants=" + flow.HasPlants);
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

        /// <summary>
        /// 种植块。两格是屏上标签，种没种、什么作物、熟没熟全认 `/homestead/plants` 与
        /// 种 / 收回包；回包里没有的格显示「空」，不拿配置顶一个状态上去。
        /// </summary>
        static void DrawPlants(UiShell shell, UiSocialFlow flow, UiPanel panel, float rightW, float plantH)
        {
            var m = shell.Metrics;

            if (!flow.HasPlants)
            {
                shell.LabelIn(panel.Root,
                    new Rect(m.Px(24f), m.Px(192f), rightW - m.Px(48f), m.Px(36f)),
                    "尚未请求 · 点「读取种植」走 /homestead/plants",
                    UiTextRole.Muted, shell.Size(21f), TextAnchor.MiddleLeft, true);
                shell.ButtonIn(panel.Root,
                    new Rect(m.Px(24f), m.Px(232f), m.Px(220f), m.Px(64f)),
                    "读取种植",
                    () => flow.LoadPlants(0, () => shell.RefreshPage()),
                    UiButtonKind.Primary);
                return;
            }

            var plants = flow.LastPlants;
            shell.ButtonIn(panel.Root,
                new Rect(m.Px(24f), m.Px(232f), m.Px(220f), m.Px(64f)),
                "读取种植",
                () => flow.LoadPlants(0, () => shell.RefreshPage()),
                UiButtonKind.Quiet);
            shell.LabelIn(panel.Root,
                new Rect(m.Px(264f), m.Px(232f), rightW - m.Px(288f), m.Px(64f)),
                plants.Ok
                    ? ("回包 " + plants.DisplayText + (plants.Duplicate ? " · 重放" : string.Empty))
                    : ("服务端：" + plants.Reason),
                plants.Ok ? UiTextRole.Muted : UiTextRole.Warm,
                shell.Size(20f), TextAnchor.MiddleLeft, false);

            var crop = UiSocialCodex.CropIds.Length > 0 ? UiSocialCodex.CropIds[0] : string.Empty;
            shell.LabelIn(panel.Root,
                new Rect(m.Px(24f), m.Px(160f), rightW - m.Px(48f), m.Px(28f)),
                "作物 " + crop + " · 编号只作请求参数，产量与时长只认回包",
                UiTextRole.Faint, shell.Size(17f), TextAnchor.MiddleLeft, false);

            if (!plants.Ok && plants.SlotCount == 0)
            {
                // 服务端说这套种植没开：不画格子，也不给「种下」，免得点了必然被拒。
                shell.LabelIn(panel.Root,
                    new Rect(m.Px(24f), m.Px(88f), rightW - m.Px(48f), m.Px(52f)),
                    "服务端未开这套种植（" + plants.Reason + "）",
                    UiTextRole.Warm, shell.Size(19f), TextAnchor.MiddleLeft, true);
                return;
            }

            for (var i = 0; i < UiSocialCodex.PlantSlots.Length; i++)
            {
                var slot = UiSocialCodex.PlantSlots[i];
                var fact = HomesteadPlantEntry.PlantInSlot(plants, slot);
                var y = m.Px(88f) - i * m.Px(60f);
                var line = fact == null
                    ? slot + " · 空"
                    : slot + " · " + fact.Value.CropId
                      + (fact.Value.Mature ? " · 已成熟" : " · 生长中 " + ReadyClock(fact.Value.ReadyAtUnix));
                shell.LabelIn(panel.Root,
                    new Rect(m.Px(24f), y, rightW * 0.62f, m.Px(52f)),
                    line, UiTextRole.Body, shell.Size(19f), TextAnchor.MiddleLeft, false);

                var slotCopy = slot;
                if (fact == null)
                {
                    shell.ButtonIn(panel.Root,
                        new Rect(rightW * 0.66f, y, m.Px(150f), m.Px(52f)),
                        "种下",
                        () =>
                        {
                            if (flow.Busy || crop.Length == 0)
                            {
                                return;
                            }

                            flow.PlantCrop(slotCopy, crop, () => shell.RefreshPage());
                        },
                        UiButtonKind.Primary);
                }
                else if (fact.Value.Mature)
                {
                    shell.ButtonIn(panel.Root,
                        new Rect(rightW * 0.66f, y, m.Px(150f), m.Px(52f)),
                        "收取",
                        () =>
                        {
                            if (flow.Busy)
                            {
                                return;
                            }

                            flow.HarvestCrop(slotCopy, () => shell.RefreshPage());
                        },
                        UiButtonKind.Primary);
                }
                else
                {
                    shell.LabelIn(panel.Root,
                        new Rect(rightW * 0.66f, y, m.Px(150f), m.Px(52f)),
                        "未熟",
                        UiTextRole.Faint, shell.Size(19f), TextAnchor.MiddleLeft, false);
                }
            }

            if (flow.HasPlantAction)
            {
                var act = flow.LastPlantAction;
                shell.LabelIn(panel.Root,
                    new Rect(rightW - m.Px(360f), plantH - m.Px(72f), m.Px(336f), m.Px(40f)),
                    act.Ok
                        ? (act.GrantedHomeCoin > 0
                            ? "本次 +" + act.GrantedHomeCoin + " 家园币"
                            : "回包：" + act.SlotId)
                        : ("被拒：" + act.Reason),
                    act.Ok ? UiTextRole.Jade : UiTextRole.Warm,
                    shell.Size(19f), TextAnchor.MiddleRight, false);
            }
        }

        /// <summary>
        /// 家园币块。余额只认收取与商城回包带的数；标价、扣多少、有没有发货同样照抄，
        /// 商城商品编号只作请求参数。
        /// </summary>
        static void DrawHomeCoin(UiShell shell, UiSocialFlow flow, UiPanel panel, float rightW)
        {
            var m = shell.Metrics;
            shell.LabelIn(panel.Root,
                new Rect(m.Px(24f), m.Px(158f), rightW - m.Px(48f), m.Px(36f)),
                flow.HasHomeCoinBalance
                    ? ("回包余额 " + flow.HomeCoinBalance + " 家园币")
                    : "余额：还没有回包带过这个数",
                UiTextRole.Body, shell.Size(22f), TextAnchor.MiddleLeft, false);

            shell.LabelIn(panel.Root,
                new Rect(m.Px(24f), m.Px(112f), rightW - m.Px(48f), m.Px(32f)),
                UiSocialCodex.PlantingNote,
                UiTextRole.Faint, shell.Size(17f), TextAnchor.MiddleLeft, false);

            shell.LabelIn(panel.Root,
                new Rect(m.Px(24f), m.Px(62f), rightW - m.Px(48f), m.Px(44f)),
                flow.HasHomeCoin
                    ? flow.LastHomeCoin.DisplayText
                      + (flow.LastHomeCoin.Duplicate ? " · 重放" : string.Empty)
                    : "尚未购买 · 商品编号只作请求参数",
                flow.HasHomeCoin && !flow.LastHomeCoin.Ok ? UiTextRole.Warm : UiTextRole.Muted,
                shell.Size(19f), TextAnchor.MiddleLeft, true);

            for (var i = 0; i < UiSocialCodex.HomeCoinShopItemIds.Length; i++)
            {
                var id = UiSocialCodex.HomeCoinShopItemIds[i];
                shell.ButtonIn(panel.Root,
                    new Rect(m.Px(24f) + i * m.Px(224f), m.Px(12f), m.Px(200f), m.Px(52f)),
                    "买 " + id,
                    () =>
                    {
                        if (flow.Busy)
                        {
                            return;
                        }

                        flow.BuyHomeCoinItem(id, () => shell.RefreshPage());
                    },
                    UiButtonKind.Normal);
            }
        }

        /// <summary>
        /// 把回包给的成熟时刻排成时:分。成熟与否仍只看回包的 <c>mature</c>，
        /// 这里只做排版，不按当前时间自己判熟没熟。
        /// </summary>
        static string ReadyClock(long readyAtUnix)
        {
            if (readyAtUnix <= 0)
            {
                return string.Empty;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(readyAtUnix)
                    .ToLocalTime().ToString("HH:mm");
            }
            catch (ArgumentOutOfRangeException)
            {
                return string.Empty;
            }
        }
    }
}

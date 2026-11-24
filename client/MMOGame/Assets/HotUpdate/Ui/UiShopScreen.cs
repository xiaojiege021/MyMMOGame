using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 商城（原型 E01）。左边是入口清单，右边是购买结果。
    /// 清单外的编号会被服务端拒绝，元宝一分不动——界面不判「买得起买不起」，
    /// 标价、扣款、余额、发什么、绑定态全看 `/shop/buy` 回包。
    /// </summary>
    public static class UiShopScreen
    {
        public const string ReadyLog = "u6_shop_screen=ready";

        public static void Build(UiShell shell, UiCommerceFlow flow)
        {
            UiUpgradeViews.Note(shell, UiCommerceCodex.ShopNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.52f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;

            var chosen = UiCommerceCodex.Shop[0].Id;
            var chosenText = shell.Label(
                new Rect(pad, m.Px(300f), leftW, m.Px(40f)),
                UiStrings.UiShopScreen.T001 + UiCommerceCodex.Shop[0].Name, UiTextRole.Faint, shell.Size(24f),
                TextAnchor.MiddleLeft, false);

            var list = shell.Panel(UiStrings.UiShopScreen.T002, m.Band(pad, top + m.Px(56f), leftW, m.Px(560f)), shell.Size(32f));
            for (var i = 0; i < UiCommerceCodex.Shop.Length; i++)
            {
                var goods = UiCommerceCodex.Shop[i];
                var opt = new UiOption(
                    list.Root,
                    new Rect(m.Px(24f), m.Px(40f) + i * m.Px(78f), leftW - m.Px(48f), m.Px(66f)),
                    goods.Id, goods.Name, goods.OneLine,
                    UiOptionKind.Card, shell.Size(26f), shell.Size(20f));
                opt.Selected = i == 0;
                var id = goods.Id;
                var name = goods.Name;
                opt.OnPick = picked =>
                {
                    chosen = id;
                    chosenText.Label.text = UiStrings.UiShopScreen.T001 + name;
                    Debug.Log("u6_shop_pick=" + id);
                };
                shell.Register(opt);
            }

            shell.Button(
                new Rect(pad, m.Px(220f), m.Px(320f), m.Px(72f)),
                UiStrings.UiShopScreen.T003,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.Buy(chosen, shell.RefreshPage);
                    }
                }, UiButtonKind.Primary);

            // 清单外拒绝：按钮只发一个不存在的编号，看回包怎么说。
            shell.Button(
                new Rect(pad + m.Px(360f), m.Px(220f), m.Px(380f), m.Px(72f)),
                UiStrings.UiShopScreen.T004,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.Buy("no_such_goods", shell.RefreshPage);
                    }
                }, UiButtonKind.Normal);

            var result = shell.Panel(UiStrings.UiShopScreen.T005, m.Band(rightX, top + m.Px(56f), rightW, m.Px(520f)),
                shell.Size(32f));
            var s = flow.LastShop;
            var has = flow.HasShop;
            UiUpgradeViews.Result(shell, result, m.Px(400f), UiStrings.UiShopScreen.T006,
                has ? (s.Ok ? UiStrings.UiShopScreen.T007 : UiStrings.UiShopScreen.T008 + s.Reason) : UiStrings.UiShopScreen.T009);
            UiUpgradeViews.Result(shell, result, m.Px(344f), UiStrings.UiShopScreen.T010,
                has ? s.PriceYuanbao + " / " + s.ChargedYuanbao : "—");
            UiUpgradeViews.Result(shell, result, m.Px(288f), UiStrings.UiShopScreen.T011,
                has ? s.Balance.ToString() : "—");
            UiUpgradeViews.Result(shell, result, m.Px(232f), UiStrings.UiShopScreen.T012,
                has ? (s.Delivered ? s.DeliveryItemId + UiStrings.UiShopScreen.T013 + s.BagSlot + UiStrings.UiShopScreen.T014 : UiStrings.UiShopScreen.T015) : "—");
            UiUpgradeViews.Result(shell, result, m.Px(176f), UiStrings.UiShopScreen.T016,
                has ? (s.Bound ? "true" : "false") : "—");
            UiUpgradeViews.Result(shell, result, m.Px(120f), UiStrings.UiShopScreen.T017,
                has ? s.State : "—");

            // ---------------------------------------------------------- K2+Q13 NPC 商店
            // 同一面板的四件事：看货架、买、卖、回购、修理。货名 / 标价 / 今日剩多少 /
            // 回收价 / 修理费全部读 /shop/npc 回包，界面不自己算一个数。
            UiUpgradeViews.Note(shell, UiNpcShopCodex.Note);

            var shops = UiContentTable.NpcShops;
            var npcShop = shops.Length > 0 ? shops[0].Id : string.Empty;
            var npcTop = m.Px(820f);
            var npcText = shell.Label(
                new Rect(pad, npcTop, m.Width - pad * 2f, m.Px(40f)),
                shops.Length > 0
                    ? UiStrings.UiShopScreen.T018 + shops[0].Name + " · " + shops[0].Id
                    : UiStrings.UiShopScreen.T019,
                UiTextRole.Faint, shell.Size(24f), TextAnchor.MiddleLeft, false);

            for (var i = 0; i < shops.Length; i++)
            {
                var shop = shops[i];
                shell.Button(
                    new Rect(pad + i * m.Px(360f), npcTop - m.Px(80f), m.Px(340f), m.Px(64f)),
                    shop.Name,
                    () =>
                    {
                        npcShop = shop.Id;
                        npcText.Label.text = UiStrings.UiShopScreen.T018 + shop.Name + " · " + shop.Id;
                        if (!flow.Busy)
                        {
                            flow.LoadNpcShop(shop.Id);
                        }
                    }, UiButtonKind.Normal);
            }

            shell.Button(
                new Rect(pad, npcTop + m.Px(56f), m.Px(240f), m.Px(68f)),
                UiStrings.UiShopScreen.T020,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.LoadNpcShop(npcShop);
                    }
                }, UiButtonKind.Primary);

            shell.Button(
                new Rect(pad + m.Px(260f), npcTop + m.Px(56f), m.Px(240f), m.Px(68f)),
                UiStrings.UiShopScreen.T021,
                () =>
                {
                    var goods = flow.HasShopNpc ? flow.ShopNpc.Goods : null;
                    if (flow.Busy || goods == null || goods.Count == 0)
                    {
                        return;
                    }

                    flow.NpcBuy(npcShop, goods[0].ItemId);
                }, UiButtonKind.Normal);

            shell.Button(
                new Rect(pad + m.Px(520f), npcTop + m.Px(56f), m.Px(240f), m.Px(68f)),
                UiStrings.UiShopScreen.T022,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.NpcSell(npcShop, 1, 1);
                    }
                }, UiButtonKind.Normal);

            shell.Button(
                new Rect(pad + m.Px(780f), npcTop + m.Px(56f), m.Px(240f), m.Px(68f)),
                UiStrings.UiShopScreen.T023,
                () =>
                {
                    if (!flow.Busy && flow.HasNpc && flow.LastNpc.BuybackId != 0)
                    {
                        flow.NpcBuyback(npcShop, flow.LastNpc.BuybackId);
                    }
                    else if (!flow.Busy)
                    {
                        flow.NpcBuyback(npcShop, 0L);
                    }
                }, UiButtonKind.Normal);

            shell.Button(
                new Rect(pad + m.Px(1040f), npcTop + m.Px(56f), m.Px(240f), m.Px(68f)),
                UiStrings.UiShopScreen.T024,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.NpcRepair();
                    }
                }, UiButtonKind.Normal);

            var npcResult = shell.Panel(UiStrings.UiShopScreen.T025, m.Band(pad, npcTop + m.Px(150f),
                m.Width - pad * 2f, m.Px(320f)), shell.Size(32f));
            var sn = flow.ShopNpc;
            var hasNpcShop = flow.HasShopNpc;
            UiUpgradeViews.Result(shell, npcResult, m.Px(220f), UiStrings.UiShopScreen.T026,
                hasNpcShop ? sn.DisplayText : UiStrings.UiShopScreen.T009);
            UiUpgradeViews.Result(shell, npcResult, m.Px(164f), UiStrings.UiShopScreen.T027,
                flow.HasNpc ? flow.LastNpc.DisplayText : UiStrings.UiShopScreen.T009);
            UiUpgradeViews.Result(shell, npcResult, m.Px(108f), UiStrings.UiShopScreen.T028,
                hasNpcShop ? UiStrings.UiShopScreen.T029 + sn.RepairPricePerPoint : "—");

            Debug.Log(UiNpcShopCodex.ReadyLog + " shops=" + shops.Length
                + " has=" + (hasNpcShop ? "true" : "false"));

            Debug.Log(ReadyLog + " goods=" + UiCommerceCodex.Shop.Length
                + " has=" + (has ? "true" : "false"));
        }
    }
}

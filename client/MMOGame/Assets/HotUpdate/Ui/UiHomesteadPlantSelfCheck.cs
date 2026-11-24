using System;
using System.Collections.Generic;
using System.Text;
using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U10 种植与家园币自检：格子与成熟事实只认 `/homestead/plants`，种 / 收与商城购买
    /// 只认回包里的 granted / charged / balance。屏上不编产量、不本地算熟没熟、不算价。
    /// </summary>
    public static class UiHomesteadPlantSelfCheck
    {
        struct PlantRow
        {
            public string SlotId;
            public string CropId;
            public long ReadyAtUnix;
            public bool Mature;
        }

        static bool _failed;

        static readonly List<PlantRow> Planted = new List<PlantRow>();

        static long _balance;

        public static bool Run()
        {
            _failed = false;
            Planted.Clear();
            _balance = 0;

            UiSkinStore.Reset();
            var shell = UiShell.Ensure();
            shell.SetSkin(UiSkin.Light, false);
            var flow = UiSocialFlow.Ensure(shell);
            flow.Bind("u10-self-token", 9);

            flow.SetSender((path, body, token, done) =>
            {
                if (path.IndexOf("/homestead/plants?", StringComparison.Ordinal) >= 0)
                {
                    done(200, PlantsJson(true, "", 0, 0));
                    return;
                }

                if (path == HomesteadPlantEntry.PlantPath)
                {
                    var slot = JsonField.Of(body, "slot_id");
                    var crop = JsonField.Of(body, "crop_id");
                    Planted.RemoveAll(p => p.SlotId == slot);
                    Planted.Add(new PlantRow
                    {
                        SlotId = slot,
                        CropId = crop,
                        ReadyAtUnix = 1763000000L,
                        Mature = false,
                    });
                    done(200, PlantsJson(true, "", 0, 0));
                    return;
                }

                if (path == HomesteadPlantEntry.HarvestPath)
                {
                    var slot = JsonField.Of(body, "slot_id");
                    Planted.RemoveAll(p => p.SlotId == slot);
                    _balance += 5;
                    done(200, PlantsJson(true, "", 5, _balance));
                    return;
                }

                if (path == HomeCoinShopEntry.BuyPath)
                {
                    var item = JsonField.Of(body, "item_id");
                    if (item == "hc_shop_outfit_1")
                    {
                        // 清单内但还没接发货：一分不扣，余额照抄。
                        done(200, "{\"ok\":false,\"reason\":\"delivery_pending\",\"item_id\":\""
                            + item + "\",\"kind\":\"outfit\",\"currency\":\"home_coin\",\"balance\":"
                            + _balance + ",\"charged_home_coin\":0,\"delivered\":false,"
                            + "\"duplicate\":false,\"txn_id\":\"txn-u10\"}");
                        return;
                    }

                    _balance -= 3;
                    done(200, "{\"ok\":true,\"reason\":\"\",\"item_id\":\"" + item
                        + "\",\"kind\":\"teleport_talisman\",\"currency\":\"home_coin\",\"balance\":"
                        + _balance + ",\"charged_home_coin\":3,\"delivered\":true,"
                        + "\"duplicate\":false,\"txn_id\":\"txn-u10\"}");
                    return;
                }

                done(404, "{}");
            });

            // 一、尚未请求：种植块与家园币块都写「尚未请求」，不拿配置顶一个状态。
            shell.ShowPage(UiStrings.UiHomesteadPlantSelfCheck.T001, s => UiHomesteadScreen.Build(s, flow));
            var pending = Text(shell);
            Report("pending", pending.Contains(UiStrings.UiHomesteadPlantSelfCheck.T002) && pending.Contains(UiStrings.UiHomesteadPlantSelfCheck.T003)
                && pending.Contains(UiStrings.UiHomesteadPlantSelfCheck.T004));
            Debug.Log("u10_self_pending=" + pending.Contains(UiStrings.UiHomesteadPlantSelfCheck.T003));

            // 二、读回包：两格空、余额仍未知（只读回包不冒充余额）。
            flow.LoadPlants();
            shell.ShowPage(UiStrings.UiHomesteadPlantSelfCheck.T001, s => UiHomesteadScreen.Build(s, flow));
            var empty = Text(shell);
            Report("empty", flow.HasPlants && flow.LastPlants.SlotCount == 2
                && empty.Contains(UiStrings.UiHomesteadPlantSelfCheck.T005) && empty.Contains(UiStrings.UiHomesteadPlantSelfCheck.T006)
                && empty.Contains(UiStrings.UiHomesteadPlantSelfCheck.T007)
                && empty.Contains(UiStrings.UiHomesteadPlantSelfCheck.T004)
                && empty.Contains(UiStrings.UiHomesteadPlantSelfCheck.T008));
            Debug.Log("u10_self_empty slots=" + flow.LastPlants.SlotCount
                + " planted=" + flow.LastPlants.Plants.Count);

            // 三、种下：格子里长出回包给的作物与生长状态，屏上写「未熟」不写秒数。
            flow.PlantCrop("garden_1", UiSocialCodex.CropIds[0]);
            shell.ShowPage(UiStrings.UiHomesteadPlantSelfCheck.T001, s => UiHomesteadScreen.Build(s, flow));
            var planted = Text(shell);
            Report("plant", flow.LastPlantAction.Ok
                && HomesteadPlantEntry.PlantInSlot(flow.LastPlants, "garden_1") != null
                && planted.Contains(UiStrings.UiHomesteadPlantSelfCheck.T009)
                && planted.Contains(UiStrings.UiHomesteadPlantSelfCheck.T010)
                && !planted.Contains(UiStrings.UiHomesteadPlantSelfCheck.T005));
            Debug.Log("u10_self_plant=" + planted.Contains(UiStrings.UiHomesteadPlantSelfCheck.T011));

            // 四、成熟与收取：熟没熟看回包的 mature，收多少与余额照抄。
            MakeMature("garden_1");
            flow.LoadPlants();
            shell.ShowPage(UiStrings.UiHomesteadPlantSelfCheck.T001, s => UiHomesteadScreen.Build(s, flow));
            Report("mature", Text(shell).Contains(UiStrings.UiHomesteadPlantSelfCheck.T012)
                && Text(shell).Contains(UiStrings.UiHomesteadPlantSelfCheck.T013));

            flow.HarvestCrop("garden_1");
            shell.ShowPage(UiStrings.UiHomesteadPlantSelfCheck.T001, s => UiHomesteadScreen.Build(s, flow));
            var harvested = Text(shell);
            Report("harvest", flow.LastPlantAction.Ok && flow.LastPlantAction.GrantedHomeCoin == 5
                && harvested.Contains(UiStrings.UiHomesteadPlantSelfCheck.T014)
                && harvested.Contains(UiStrings.UiHomesteadPlantSelfCheck.T015)
                && harvested.Contains(UiStrings.UiHomesteadPlantSelfCheck.T005));
            Debug.Log("u10_self_harvest balance=" + flow.HomeCoinBalance);

            // 五、商城：扣多少与余额只认回包；没接发货的一分不扣。
            flow.BuyHomeCoinItem("hc_shop_teleport_1");
            shell.ShowPage(UiStrings.UiHomesteadPlantSelfCheck.T001, s => UiHomesteadScreen.Build(s, flow));
            var bought = Text(shell);
            Report("buy", flow.LastHomeCoin.Ok && flow.LastHomeCoin.ChargedHomeCoin == 3
                && bought.Contains(UiStrings.UiHomesteadPlantSelfCheck.T016)
                && bought.Contains(UiStrings.UiHomesteadPlantSelfCheck.T017));
            Debug.Log("u10_self_buy=" + flow.LastHomeCoin.DisplayText);

            flow.BuyHomeCoinItem("hc_shop_outfit_1");
            shell.ShowPage(UiStrings.UiHomesteadPlantSelfCheck.T001, s => UiHomesteadScreen.Build(s, flow));
            var pendingBuy = Text(shell);
            Report("buy_pending", !flow.LastHomeCoin.Ok
                && flow.LastHomeCoin.Reason == "delivery_pending"
                && pendingBuy.Contains("delivery_pending"));

            // 六、服务端说这套种植没开：不画格子，也不给种下。
            flow.SetSender((path, body, token, done) =>
            {
                done(200, "{\"ok\":false,\"reason\":\"plant_unavailable\",\"slot_count\":0,"
                    + "\"plants\":[],\"character_id\":0,\"slot_id\":\"\",\"crop_id\":\"\","
                    + "\"txn_id\":\"\",\"duplicate\":false,\"granted_home_coin\":0,"
                    + "\"home_coin_balance\":0}");
            });
            flow.LoadPlants();
            shell.ShowPage(UiStrings.UiHomesteadPlantSelfCheck.T001, s => UiHomesteadScreen.Build(s, flow));
            var closed = Text(shell);
            Report("unavailable", closed.Contains(UiStrings.UiHomesteadPlantSelfCheck.T018)
                && !closed.Contains(UiStrings.UiHomesteadPlantSelfCheck.T005));
            Debug.Log("u10_self_unavailable=" + closed.Contains("plant_unavailable"));

            // 七、切皮肤只换颜色：布局与文案一字不差。
            shell.ShowPage(UiStrings.UiHomesteadPlantSelfCheck.T001, s => UiHomesteadScreen.Build(s, flow));
            var layout = Layout(shell);
            var copy = Text(shell);
            shell.SetSkin(UiSkin.Dark, false);
            Report("skin_layout", layout == Layout(shell));
            Report("skin_copy", copy == Text(shell));

            shell.ClosePage();
            UiSkinStore.Reset();
            if (flow)
            {
                UnityEngine.Object.DestroyImmediate(flow.gameObject);
            }

            if (shell)
            {
                try { UnityEngine.Object.DestroyImmediate(shell.gameObject); }
                catch (Exception) { }
            }

            Debug.Log("u10_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        static void MakeMature(string slotId)
        {
            for (var i = 0; i < Planted.Count; i++)
            {
                if (Planted[i].SlotId != slotId)
                {
                    continue;
                }

                var row = Planted[i];
                row.Mature = true;
                Planted[i] = row;
            }
        }

        static string PlantsJson(bool ok, string reason, long granted, long balance)
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":").Append(ok ? "true" : "false")
                .Append(",\"reason\":\"").Append(reason)
                .Append("\",\"character_id\":9,\"slot_id\":\"\",\"crop_id\":\"\",")
                .Append("\"txn_id\":\"txn-u10\",\"duplicate\":false,\"slot_count\":2,")
                .Append("\"granted_home_coin\":").Append(granted)
                .Append(",\"home_coin_balance\":").Append(balance)
                .Append(",\"plants\":[");
            for (var i = 0; i < Planted.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"slot_id\":\"").Append(Planted[i].SlotId)
                    .Append("\",\"crop_id\":\"").Append(Planted[i].CropId)
                    .Append("\",\"planted_at_unix\":1762999880,\"ready_at_unix\":")
                    .Append(Planted[i].ReadyAtUnix)
                    .Append(",\"mature\":").Append(Planted[i].Mature ? "true" : "false").Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        static string Text(UiShell shell)
        {
            var sb = new StringBuilder();
            var labels = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                if (labels[i] != null && labels[i].text.Length > 0)
                {
                    sb.AppendLine(labels[i].text);
                }
            }

            return sb.ToString();
        }

        static string Layout(UiShell shell)
        {
            var sb = new StringBuilder();
            var rects = shell.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < rects.Length; i++)
            {
                var r = rects[i];
                sb.Append(r.name).Append(':')
                    .Append(r.anchoredPosition.x.ToString("0")).Append(',')
                    .Append(r.anchoredPosition.y.ToString("0")).Append(',')
                    .Append(r.sizeDelta.x.ToString("0")).Append(',')
                    .Append(r.sizeDelta.y.ToString("0")).Append(';');
            }

            return sb.ToString();
        }

        static void Report(string key, bool ok)
        {
            Debug.Log("u10_self_" + key + "=" + (ok ? "ok" : "fail"));
            if (!ok) _failed = true;
        }
    }
}

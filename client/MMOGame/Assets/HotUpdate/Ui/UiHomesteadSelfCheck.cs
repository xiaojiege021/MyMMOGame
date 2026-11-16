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
    /// U9 洞府屏自检：读 /homestead 事实值、摆 / 撤走 Hub；种植块由 U10 的
    /// UiHomesteadPlantSelfCheck 另测，这里只认「尚未请求」与读取入口在。
    /// </summary>
    public static class UiHomesteadSelfCheck
    {
        static bool _failed;

        public static bool Run()
        {
            _failed = false;
            UiSkinStore.Reset();
            var shell = UiShell.Ensure();
            shell.SetSkin(UiSkin.Light, false);
            var flow = UiSocialFlow.Ensure(shell);
            flow.Bind("u9-self-token", 9);

            var placed = new List<HomesteadPlacementView>();
            flow.SetSender((path, body, token, done) =>
            {
                if (path.IndexOf("/homestead?", StringComparison.Ordinal) >= 0
                    || path == HomesteadEntry.DescribePath(9))
                {
                    done(200, DescribeJson(placed, 6, true, ""));
                    return;
                }

                if (path == HomesteadEntry.PlacePath)
                {
                    var slot = JsonField.Of(body, "slot_id");
                    var item = JsonField.Of(body, "item_id");
                    placed.RemoveAll(p => p.SlotId == slot || p.ItemId == item);
                    placed.Add(new HomesteadPlacementView { SlotId = slot, ItemId = item });
                    done(200, DescribeJson(placed, 6, true, "", slot, item));
                    return;
                }

                if (path == HomesteadEntry.RemovePath)
                {
                    var slot = JsonField.Of(body, "slot_id");
                    placed.RemoveAll(p => p.SlotId == slot);
                    done(200, DescribeJson(placed, 6, true, "", slot, ""));
                    return;
                }

                if (path.IndexOf("/inventory", StringComparison.Ordinal) >= 0)
                {
                    done(200, "{\"bag_slots\":20,\"character_name\":\"洞府自检\",\"realm\":\"炼体\","
                        + "\"slots\":[{\"slot\":1,\"item_id\":\"decor_s2\",\"count\":1,\"bound\":true},"
                        + "{\"slot\":2,\"item_id\":\"ore_iron\",\"count\":2,\"bound\":false}]}");
                    return;
                }

                done(404, "{}");
            });

            // 尚未请求时有读取按钮，种植块也如实写「尚未请求」。
            shell.ShowPage("家园洞府", s => UiHomesteadScreen.Build(s, flow));
            var pending = Text(shell);
            Report("pending", pending.Contains("尚未请求") && pending.Contains("读取种植"));
            Debug.Log("u9_self_pending pending=" + pending.Contains("尚未请求"));

            flow.LoadHomestead();
            flow.LoadHomesteadBag();
            shell.ShowPage("家园洞府", s => UiHomesteadScreen.Build(s, flow));
            var empty = Text(shell);
            Report("empty", flow.HasHomestead && flow.LastHomestead.SlotCount == 6
                && empty.Contains("court_1 · 空") && empty.Contains("hall_1 · 空")
                && empty.Contains("decor_s2") && empty.Contains("读取种植")
                && !empty.Contains("无接口"));
            Debug.Log("u9_self_empty slots=" + flow.LastHomestead.SlotCount
                + " bag=" + flow.DecorInBag().Count);

            flow.PlaceDecor("hall_1", "decor_s2");
            shell.ShowPage("家园洞府", s => UiHomesteadScreen.Build(s, flow));
            var after = Text(shell);
            Report("place", flow.LastHomestead.Ok
                && HomesteadEntry.ItemInSlot(flow.LastHomestead, "hall_1") == "decor_s2"
                && after.Contains("hall_1 · decor_s2"));
            Debug.Log("u9_self_place item=" + HomesteadEntry.ItemInSlot(flow.LastHomestead, "hall_1"));

            flow.RemoveDecor("hall_1");
            shell.ShowPage("家园洞府", s => UiHomesteadScreen.Build(s, flow));
            Report("remove", HomesteadEntry.ItemInSlot(flow.LastHomestead, "hall_1").Length == 0
                && Text(shell).Contains("hall_1 · 空"));

            // 拒绝：unavailable
            flow.SetSender((path, body, token, done) =>
            {
                done(200, "{\"ok\":false,\"reason\":\"homestead_unavailable\",\"slot_count\":0,"
                    + "\"placements\":[],\"character_id\":0,\"slot_id\":\"\",\"item_id\":\"\","
                    + "\"txn_id\":\"\",\"duplicate\":false}");
            });
            flow.LoadHomestead();
            shell.ShowPage("家园洞府", s => UiHomesteadScreen.Build(s, flow));
            Report("unavailable", Text(shell).Contains("homestead_unavailable"));

            shell.ShowPage("家园洞府", s => UiHomesteadScreen.Build(s, flow));
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

            Debug.Log("u9_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        static string DescribeJson(
            List<HomesteadPlacementView> placed, int slotCount, bool ok, string reason,
            string slotId = "", string itemId = "")
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":").Append(ok ? "true" : "false")
                .Append(",\"reason\":\"").Append(reason)
                .Append("\",\"character_id\":9,\"slot_id\":\"").Append(slotId)
                .Append("\",\"item_id\":\"").Append(itemId)
                .Append("\",\"txn_id\":\"txn-u9\",\"duplicate\":false,\"slot_count\":")
                .Append(slotCount).Append(",\"placements\":[");
            for (var i = 0; i < placed.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"slot_id\":\"").Append(placed[i].SlotId)
                    .Append("\",\"item_id\":\"").Append(placed[i].ItemId).Append("\"}");
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
            Debug.Log("u9_self_" + key + "=" + (ok ? "ok" : "fail"));
            if (!ok) _failed = true;
        }
    }
}

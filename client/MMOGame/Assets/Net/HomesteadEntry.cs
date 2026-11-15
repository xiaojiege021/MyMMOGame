using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mmo.Net
{
    /// <summary>一格摆放事实。格位与物品编号都是服务端给的，客户端不猜。</summary>
    public struct HomesteadPlacementView
    {
        public string SlotId;
        public string ItemId;
    }

    /// <summary>
    /// 洞府摆放回包（C3 的 `GET /homestead` 与 `POST /homestead/place|remove`）。
    /// 格位数、摆了哪几件、拒因全部来自社交服；客户端不读家园配置表。
    /// </summary>
    public struct HomesteadOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong CharacterId;
        public string SlotId;
        public string ItemId;
        public string TxnId;
        public bool Duplicate;
        public int SlotCount;
        public List<HomesteadPlacementView> Placements;
        public string DisplayText;

        public static HomesteadOutcome Failure(string reason) => new HomesteadOutcome
        {
            Ok = false,
            Reason = reason ?? string.Empty,
            Placements = new List<HomesteadPlacementView>(),
            DisplayText = "没读到：" + (reason ?? string.Empty),
        };
    }

    /// <summary>`/homestead*` 的路径、请求体与回包拆装。</summary>
    public static class HomesteadEntry
    {
        public static string DescribePath(ulong ownerCharacterId) =>
            "/homestead?owner=" + ownerCharacterId.ToString(CultureInfo.InvariantCulture);

        public const string PlacePath = "/homestead/place";

        public const string RemovePath = "/homestead/remove";

        public static string PlaceBody(ulong characterId, string slotId, string itemId)
        {
            var sb = new StringBuilder(96);
            sb.Append("{\"character_id\":").Append(characterId.ToString(CultureInfo.InvariantCulture))
                .Append(",\"slot_id\":\"").Append(Escape(slotId))
                .Append("\",\"item_id\":\"").Append(Escape(itemId)).Append("\"}");
            return sb.ToString();
        }

        public static string RemoveBody(ulong characterId, string slotId)
        {
            var sb = new StringBuilder(64);
            sb.Append("{\"character_id\":").Append(characterId.ToString(CultureInfo.InvariantCulture))
                .Append(",\"slot_id\":\"").Append(Escape(slotId)).Append("\"}");
            return sb.ToString();
        }

        public static HomesteadOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return HomesteadOutcome.Failure("bad_reply");
            }

            var ok = string.Equals(JsonField.Of(json, "ok"), "true", StringComparison.OrdinalIgnoreCase);
            var reason = JsonField.Of(json, "reason");
            var placements = ParsePlacements(json);
            var slotCount = (int)ReadLong(json, "slot_count");
            var text = ok
                ? ("格位 " + slotCount + " · 已摆 " + placements.Count)
                : ("没成：" + (reason.Length == 0 ? "未知原因" : reason));

            return new HomesteadOutcome
            {
                Ok = ok,
                Reason = reason,
                CharacterId = (ulong)ReadLong(json, "character_id"),
                SlotId = JsonField.Of(json, "slot_id"),
                ItemId = JsonField.Of(json, "item_id"),
                TxnId = JsonField.Of(json, "txn_id"),
                Duplicate = string.Equals(JsonField.Of(json, "duplicate"), "true",
                    StringComparison.OrdinalIgnoreCase),
                SlotCount = slotCount,
                Placements = placements,
                DisplayText = text,
            };
        }

        /// <summary>某一格当前摆了什么。空格回空串。</summary>
        public static string ItemInSlot(HomesteadOutcome outcome, string slotId)
        {
            if (outcome.Placements == null || string.IsNullOrEmpty(slotId))
            {
                return string.Empty;
            }

            for (var i = 0; i < outcome.Placements.Count; i++)
            {
                if (string.Equals(outcome.Placements[i].SlotId, slotId, StringComparison.Ordinal))
                {
                    return outcome.Placements[i].ItemId ?? string.Empty;
                }
            }

            return string.Empty;
        }

        static List<HomesteadPlacementView> ParsePlacements(string json)
        {
            var rows = new List<HomesteadPlacementView>();
            var start = json.IndexOf("\"placements\"", StringComparison.Ordinal);
            if (start < 0)
            {
                return rows;
            }

            start = json.IndexOf('[', start);
            var end = start >= 0 ? json.IndexOf(']', start) : -1;
            if (start < 0 || end < 0)
            {
                return rows;
            }

            var at = start;
            while (true)
            {
                var open = json.IndexOf('{', at);
                if (open < 0 || open > end)
                {
                    break;
                }

                var close = json.IndexOf('}', open);
                if (close < 0 || close > end)
                {
                    break;
                }

                var one = json.Substring(open, close - open + 1);
                rows.Add(new HomesteadPlacementView
                {
                    SlotId = JsonField.Of(one, "slot_id"),
                    ItemId = JsonField.Of(one, "item_id"),
                });
                at = close + 1;
            }

            return rows;
        }

        static long ReadLong(string json, string key)
        {
            long value;
            return long.TryParse(JsonField.Of(json, key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value)
                ? value
                : 0L;
        }

        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}

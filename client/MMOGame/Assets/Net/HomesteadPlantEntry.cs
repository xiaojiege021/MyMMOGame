using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mmo.Net
{
    /// <summary>一格种植事实。作物编号、种下时刻、成熟时刻与成熟与否都是服务端给的。</summary>
    public struct HomesteadPlantView
    {
        public string SlotId;
        public string CropId;
        public long PlantedAtUnix;
        public long ReadyAtUnix;
        public bool Mature;
    }

    /// <summary>
    /// 种植回包（N11 的 `GET /homestead/plants` 与 `POST /homestead/plant|harvest`）。
    ///
    /// 格位数、种了什么、熟没熟、这次收了多少家园币，全部来自社交服；产量与成长时长在
    /// 服务端配置里，客户端不读、也不算。请求体里不带产量、不带家园币，事务号由网关生成。
    /// </summary>
    public struct HomesteadPlantOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong CharacterId;
        public string SlotId;
        public string CropId;
        public string TxnId;
        public bool Duplicate;
        public int SlotCount;
        public long GrantedHomeCoin;
        public long HomeCoinBalance;
        public List<HomesteadPlantView> Plants;
        public string DisplayText;

        public static HomesteadPlantOutcome Failure(string reason) => new HomesteadPlantOutcome
        {
            Ok = false,
            Reason = reason ?? string.Empty,
            Plants = new List<HomesteadPlantView>(),
            DisplayText = "没读到：" + (reason ?? string.Empty),
        };
    }

    /// <summary>`/homestead/plants*` 的路径、请求体与回包拆装。</summary>
    public static class HomesteadPlantEntry
    {
        public const string PlantPath = "/homestead/plant";

        public const string HarvestPath = "/homestead/harvest";

        public static string DescribePath(ulong ownerCharacterId) =>
            "/homestead/plants?owner=" + ownerCharacterId.ToString(CultureInfo.InvariantCulture);

        public static string PlantBody(ulong characterId, string slotId, string cropId)
        {
            var sb = new StringBuilder(96);
            sb.Append("{\"character_id\":").Append(characterId.ToString(CultureInfo.InvariantCulture))
                .Append(",\"slot_id\":\"").Append(Escape(slotId))
                .Append("\",\"crop_id\":\"").Append(Escape(cropId)).Append("\"}");
            return sb.ToString();
        }

        public static string HarvestBody(ulong characterId, string slotId)
        {
            var sb = new StringBuilder(64);
            sb.Append("{\"character_id\":").Append(characterId.ToString(CultureInfo.InvariantCulture))
                .Append(",\"slot_id\":\"").Append(Escape(slotId)).Append("\"}");
            return sb.ToString();
        }

        public static HomesteadPlantOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return HomesteadPlantOutcome.Failure("bad_reply");
            }

            var ok = string.Equals(JsonField.Of(json, "ok"), "true", StringComparison.OrdinalIgnoreCase);
            var reason = JsonField.Of(json, "reason");
            var plants = ParsePlants(json);
            var slotCount = (int)ReadLong(json, "slot_count");
            var granted = ReadLong(json, "granted_home_coin");
            var balance = ReadLong(json, "home_coin_balance");
            var text = ok
                ? ("格位 " + slotCount + " · 已种 " + plants.Count)
                : ("没成：" + (reason.Length == 0 ? "未知原因" : reason));

            return new HomesteadPlantOutcome
            {
                Ok = ok,
                Reason = reason,
                CharacterId = (ulong)ReadLong(json, "character_id"),
                SlotId = JsonField.Of(json, "slot_id"),
                CropId = JsonField.Of(json, "crop_id"),
                TxnId = JsonField.Of(json, "txn_id"),
                Duplicate = string.Equals(JsonField.Of(json, "duplicate"), "true",
                    StringComparison.OrdinalIgnoreCase),
                SlotCount = slotCount,
                GrantedHomeCoin = granted,
                HomeCoinBalance = balance,
                Plants = plants,
                DisplayText = text,
            };
        }

        /// <summary>某一格现在种着什么。空格回 <c>null</c>。</summary>
        public static HomesteadPlantView? PlantInSlot(HomesteadPlantOutcome outcome, string slotId)
        {
            if (outcome.Plants == null || string.IsNullOrEmpty(slotId))
            {
                return null;
            }

            for (var i = 0; i < outcome.Plants.Count; i++)
            {
                if (string.Equals(outcome.Plants[i].SlotId, slotId, StringComparison.Ordinal))
                {
                    return outcome.Plants[i];
                }
            }

            return null;
        }

        static List<HomesteadPlantView> ParsePlants(string json)
        {
            var rows = new List<HomesteadPlantView>();
            var start = json.IndexOf("\"plants\"", StringComparison.Ordinal);
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
                rows.Add(new HomesteadPlantView
                {
                    SlotId = JsonField.Of(one, "slot_id"),
                    CropId = JsonField.Of(one, "crop_id"),
                    PlantedAtUnix = ReadLong(one, "planted_at_unix"),
                    ReadyAtUnix = ReadLong(one, "ready_at_unix"),
                    Mature = string.Equals(JsonField.Of(one, "mature"), "true",
                        StringComparison.OrdinalIgnoreCase),
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

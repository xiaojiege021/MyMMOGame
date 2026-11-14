using System;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>
    /// 世界 Boss 入口的只读事实值（P3-5a 的 `GET /world/worldboss`）。
    /// 谁记在入口上、什么时刻开、上限多少，全部由世界服在它自己的进程内存里回答；
    /// 客户端不推断、不按本地时钟判开没开。
    /// </summary>
    public struct WorldBossFacts
    {
        public bool Visible;
        public string Reason;
        public string EntranceId;
        public string EntranceName;
        public string WindowKey;
        public int Cap;
        public bool Killed;
        public ulong OwnerSectId;
        public string OwnerSectName;
        public long StartsAtMs;
        public long EndsAtMs;
        public long NextStartsAtMs;
        public string DisplayText;
    }

    /// <summary>`/world/worldboss` 的回包拆装。一个数都不在这里算。</summary>
    public static class WorldBossEntry
    {
        public const string RequestPath = "/world/worldboss";

        public static WorldBossFacts Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "visible").Length == 0)
            {
                return new WorldBossFacts
                {
                    Visible = false,
                    Reason = "bad_reply",
                    DisplayText = "回包读不出来",
                };
            }

            var visible = ReadBool(json, "visible");
            var killed = ReadBool(json, "killed");
            var owner = JsonField.Of(json, "owner_sect_name");
            var entrance = JsonField.Of(json, "entrance_name");

            var text = visible
                ? (entrance.Length == 0 ? "入口未定" : entrance)
                    + (killed ? " · 这一场已打掉" : " · 这一场还开着")
                : "这一刻没开：" + JsonField.Of(json, "reason");

            return new WorldBossFacts
            {
                Visible = visible,
                Reason = JsonField.Of(json, "reason"),
                EntranceId = JsonField.Of(json, "entrance_id"),
                EntranceName = entrance,
                WindowKey = JsonField.Of(json, "window_key"),
                Cap = (int)ReadLong(json, "cap"),
                Killed = killed,
                OwnerSectId = (ulong)ReadLong(json, "owner_sect_id"),
                OwnerSectName = owner,
                StartsAtMs = ReadLong(json, "starts_at_ms"),
                EndsAtMs = ReadLong(json, "ends_at_ms"),
                NextStartsAtMs = ReadLong(json, "next_starts_at_ms"),
                DisplayText = text,
            };
        }

        /// <summary>入口上记的名字。没宗门记过就给空串，屏上写「无人记名」，不编一个。</summary>
        public static string OwnerLine(WorldBossFacts facts)
        {
            return string.IsNullOrEmpty(facts.OwnerSectName) ? "无人记名" : facts.OwnerSectName;
        }

        static bool ReadBool(string json, string key) =>
            string.Equals(JsonField.Of(json, key), "true", StringComparison.OrdinalIgnoreCase);

        static long ReadLong(string json, string key)
        {
            long value;
            return long.TryParse(JsonField.Of(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : 0L;
        }
    }
}

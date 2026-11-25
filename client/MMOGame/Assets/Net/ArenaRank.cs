using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>一档段位。`reached` 与 `claimed` 是角色服算好的判定结果，客户端不自己比分数。</summary>
    public struct ArenaTier
    {
        public string Key;
        public string Name;
        public int MinScore;
        public string AppearanceItemId;
        public bool Reached;
        public bool Claimed;
    }

    /// <summary>
    /// B11 段位只读结果（`GET /arena/rank`）。分数、当前档、下一档门槛、每档领没领
    /// 全部照抄回包；季归全局服务，客户端带不进季。
    /// </summary>
    public struct ArenaRankOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong SeasonId;
        public int Score;
        public string TierKey;
        public string TierName;
        public int Wins;
        public string NextTierKey;
        public int NextTierMinScore;

        /// <summary>档位表，顺序就是回包顺序（从低到高由角色服排好，客户端不重排）。</summary>
        public List<ArenaTier> Tiers;

        public static ArenaRankOutcome Failure(string reason) => new ArenaRankOutcome
        {
            Ok = false,
            Reason = reason,
            Tiers = new List<ArenaTier>(),
        };
    }

    /// <summary>排行榜一行。名次是回包给的，客户端不按分数重排。</summary>
    public struct ArenaBoardRow
    {
        public int Rank;
        public ulong CharacterId;
        public string Name;
        public int ClassId;
        public int Score;
        public string TierKey;
        public string TierName;
    }

    /// <summary>B11 排行榜只读结果（`GET /arena/leaderboard`）。`limit` 是角色服按配置截断后的实际条数。</summary>
    public struct ArenaBoardOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong SeasonId;
        public int Limit;
        public List<ArenaBoardRow> Rows;

        public static ArenaBoardOutcome Failure(string reason) => new ArenaBoardOutcome
        {
            Ok = false,
            Reason = reason,
            Rows = new List<ArenaBoardRow>(),
        };
    }

    /// <summary>B11 领段位外观的结果（`POST /arena/appearance/claim`）。`duplicate` 是同一档重放。</summary>
    public struct ArenaClaimOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong SeasonId;
        public string TierKey;
        public string ItemId;
        public int BagSlot;
        public bool Bound;
        public bool Duplicate;
        public string DisplayText;

        public static ArenaClaimOutcome Failure(string reason) => new ArenaClaimOutcome
        {
            Ok = false,
            Reason = reason,
            DisplayText = "回包读不出来：" + reason,
        };
    }

    /// <summary>
    /// B11：竞技场赛季段位的三条 HTTP 口拆装。
    /// 段位走 `GET /arena/rank`、排行走 `GET /arena/leaderboard`、领外观走
    /// `POST /arena/appearance/claim`。
    ///
    /// 客户端只发角色号与要领的档位键：分数怎么加、档位门槛、哪一档够不够、
    /// 这一季领没领过全在角色服的段位配置表与库里判；事务号由网关生成。
    /// 这里不读配置表、不比分数门槛、不排序、不推算「还差多少分」。
    /// Mmo.Net 是 noEngineReferences 的纯 .NET 程序集，不引引擎里的 JSON 组件。
    /// </summary>
    public static class ArenaRank
    {
        /// <summary>段位只读口。归属由网关按令牌复核，这里只带角色编号。</summary>
        public const string RankPath = "/arena/rank";

        public static string RankQuery(ulong characterId) =>
            RankPath + "?character_id=" + characterId.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// 排行榜只读口。不带 `limit`：前多少名由角色服按配置截断，
        /// 客户端「只能要更少」，这一版不要更少，认服务端给的条数。
        /// </summary>
        public const string BoardPath = "/arena/leaderboard";

        /// <summary>领段位专属外观。门槛只认库里记下的分数，一季一档只领一次。</summary>
        public const string ClaimPath = "/arena/appearance/claim";

        public static string ClaimBody(ulong characterId, string tierKey)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"tier_key\":\"" + Escape(tierKey ?? string.Empty) + "\"}";
        }

        public static ArenaRankOutcome ParseRank(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return ArenaRankOutcome.Failure("bad_reply");
            }

            var outcome = new ArenaRankOutcome
            {
                Ok = ReadBool(json, "ok"),
                Reason = Raw(json, "reason"),
                SeasonId = ReadULong(json, "season_id"),
                Score = ReadInt(json, "score"),
                TierKey = Raw(json, "tier_key"),
                TierName = Raw(json, "tier_name"),
                Wins = ReadInt(json, "wins"),
                NextTierKey = Raw(json, "next_tier_key"),
                NextTierMinScore = ReadInt(json, "next_tier_min_score"),
                Tiers = new List<ArenaTier>(),
            };

            // 档位表：按回包顺序逐档抄，不按 min_score 重排、不补档、不删档。
            var items = Objects(json, "tiers");
            for (var i = 0; i < items.Count; i++)
            {
                var one = items[i];
                outcome.Tiers.Add(new ArenaTier
                {
                    Key = Raw(one, "key"),
                    Name = Raw(one, "name"),
                    MinScore = ReadInt(one, "min_score"),
                    AppearanceItemId = Raw(one, "appearance_item_id"),
                    Reached = ReadBool(one, "reached"),
                    Claimed = ReadBool(one, "claimed"),
                });
            }

            return outcome;
        }

        public static ArenaBoardOutcome ParseBoard(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return ArenaBoardOutcome.Failure("bad_reply");
            }

            var outcome = new ArenaBoardOutcome
            {
                Ok = ReadBool(json, "ok"),
                Reason = Raw(json, "reason"),
                SeasonId = ReadULong(json, "season_id"),
                Limit = ReadInt(json, "limit"),
                Rows = new List<ArenaBoardRow>(),
            };

            var items = Objects(json, "entries");
            for (var i = 0; i < items.Count; i++)
            {
                var one = items[i];
                outcome.Rows.Add(new ArenaBoardRow
                {
                    Rank = ReadInt(one, "rank"),
                    CharacterId = ReadULong(one, "character_id"),
                    Name = Raw(one, "name"),
                    ClassId = ReadInt(one, "class_id"),
                    Score = ReadInt(one, "score"),
                    TierKey = Raw(one, "tier_key"),
                    TierName = Raw(one, "tier_name"),
                });
            }

            return outcome;
        }

        public static ArenaClaimOutcome ParseClaim(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return ArenaClaimOutcome.Failure("bad_reply");
            }

            var ok = ReadBool(json, "ok");
            var reason = Raw(json, "reason");
            var duplicate = ReadBool(json, "duplicate");
            var tierKey = Raw(json, "tier_key");
            var itemId = Raw(json, "item_id");
            var bagSlot = ReadInt(json, "bag_slot");

            string text;
            if (!ok)
            {
                // 拒绝码原样带出来：界面不换成自己的说法。
                text = "领取未生效：" + reason;
            }
            else if (duplicate)
            {
                text = "这一档已经领过，不重复发";
            }
            else
            {
                text = "已领到 " + itemId + "，放进第 " + bagSlot.ToString(CultureInfo.InvariantCulture) + " 格";
            }

            return new ArenaClaimOutcome
            {
                Ok = ok,
                Reason = reason,
                SeasonId = ReadULong(json, "season_id"),
                TierKey = tierKey,
                ItemId = itemId,
                BagSlot = bagSlot,
                Bound = ReadBool(json, "bound"),
                Duplicate = duplicate,
                DisplayText = text,
            };
        }

        /// <summary>一档的标签：名字优先，没名字退回键。</summary>
        public static string TierLabel(string key, string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            return key ?? string.Empty;
        }

        static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        static bool ReadBool(string json, string key) =>
            string.Equals(Raw(json, key), "true", StringComparison.OrdinalIgnoreCase);

        static int ReadInt(string json, string key)
        {
            int.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        static ulong ReadULong(string json, string key)
        {
            ulong.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        /// <summary>
        /// 取一个字段的原始文本。回包字段固定，够用即可，不做通用 JSON 解析。
        ///
        /// 命中后要求下一个非空白字符是冒号：段位回包里 `tier_key` 的**值**就叫
        /// `gold`、排行榜里 `tier_name` 的值就是档名，直接按裸串找会先命中值。
        /// （与 <c>CharacterAttr</c> 同一道判，A9 那张卡踩过。）
        /// </summary>
        static string Raw(string json, string key)
        {
            var needle = "\"" + key + "\"";
            var at = 0;
            int colon;
            while (true)
            {
                at = json.IndexOf(needle, at, StringComparison.Ordinal);
                if (at < 0)
                {
                    return string.Empty;
                }

                colon = at + needle.Length;
                while (colon < json.Length && char.IsWhiteSpace(json[colon]))
                {
                    colon++;
                }

                if (colon < json.Length && json[colon] == ':')
                {
                    break;
                }

                at += needle.Length;
            }

            var i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i]))
            {
                i++;
            }

            if (i >= json.Length)
            {
                return string.Empty;
            }

            if (json[i] == '"')
            {
                i++;
                var start = i;
                while (i < json.Length)
                {
                    if (json[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (json[i] == '"')
                    {
                        break;
                    }

                    i++;
                }

                return json.Substring(start, Math.Min(i, json.Length) - start);
            }

            var end = i;
            while (end < json.Length && json[end] != ',' && json[end] != '}'
                   && !char.IsWhiteSpace(json[end]))
            {
                end++;
            }

            return json.Substring(i, end - i);
        }

        /// <summary>
        /// 取一个数组字段里的顶层对象。只做「定位 [ ... ] 再按顶层 { } 切段」，
        /// 不解析嵌套对象内部的键，够这两条只读口用。
        /// </summary>
        static List<string> Objects(string json, string key)
        {
            var items = new List<string>();
            var inner = ArrayBody(json, key);
            if (inner.Length == 0)
            {
                return items;
            }

            var depth = 0;
            var start = -1;
            var inString = false;
            for (var i = 0; i < inner.Length; i++)
            {
                var c = inner[i];
                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{')
                {
                    if (depth == 0)
                    {
                        start = i;
                    }

                    depth++;
                    continue;
                }

                if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        items.Add(inner.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }

            return items;
        }

        /// <summary>数组字段的方括号内部文本；没有这个字段或不是数组就给空串。</summary>
        static string ArrayBody(string json, string key)
        {
            var needle = "\"" + key + "\"";
            var at = 0;
            int colon;
            while (true)
            {
                at = json.IndexOf(needle, at, StringComparison.Ordinal);
                if (at < 0)
                {
                    return string.Empty;
                }

                colon = at + needle.Length;
                while (colon < json.Length && char.IsWhiteSpace(json[colon]))
                {
                    colon++;
                }

                if (colon < json.Length && json[colon] == ':')
                {
                    break;
                }

                at += needle.Length;
            }

            var i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i]))
            {
                i++;
            }

            if (i >= json.Length || json[i] != '[')
            {
                return string.Empty;
            }

            var depth = 0;
            var inString = false;
            var start = i + 1;
            for (; i < json.Length; i++)
            {
                var c = json[i];
                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '[' || c == '{')
                {
                    depth++;
                    continue;
                }

                if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return json.Substring(start, i - start);
                    }
                }
            }

            return string.Empty;
        }
    }
}

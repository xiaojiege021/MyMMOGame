using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>建宗 / 收人的回包。等级、名册上限、名册人数都是社交服给的事实值。</summary>
    public struct SectOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong SectId;
        public int Level;
        public int MemberCap;
        public int MemberCount;
        public string Name;
        public string TxnId;
        public string DisplayText;
    }

    /// <summary>任命 / 免职的回包。席位上限与已占席位是社交服按宗门等级给的，客户端带不进去。</summary>
    public struct PositionOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong SectId;
        public string Position;
        public int SeatCap;
        public int SeatCount;
        public string TxnId;
        public string DisplayText;
    }

    /// <summary>传位 / 退出的回包。掌门编号与三行计数都是事实值，拒绝时给 0。</summary>
    public struct LeadershipOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong SectId;
        public ulong OldMaster;
        public ulong NewMaster;
        public int MasterCount;
        public int ViceMasterCount;
        public int MemberCount;
        public string TxnId;
        public string DisplayText;
    }

    /// <summary>名册里的一行。只有角色号和职位，都是回包原文。</summary>
    public struct RosterMember
    {
        public ulong CharacterId;
        public string Position;
    }

    /// <summary>
    /// 名册只读回包（N2 的 GET /sect/roster）。
    /// 没有人数上限、没有席位数字；成员顺序与回包数组一致。
    /// </summary>
    public struct RosterOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong SectId;
        public List<RosterMember> Members;
        public string DisplayText;
    }

    /// <summary>升级的回包。升到几级、上限、建设前后与消耗全是事实值。</summary>
    public struct UpgradeOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong SectId;
        public int OldLevel;
        public int NewLevel;
        public int MemberCap;
        public long ConstructionBefore;
        public long ConstructionAfter;
        public long UpgradeCost;
        public string TxnId;
        public string DisplayText;
    }

    /// <summary>
    /// 宗门请求与回包拆装（P2-1b 的 7 条 HTTP 入口）。
    ///
    /// 请求里只放角色、宗门、目标与职位名：升到几级、扣多少建设、席位数字、
    /// 名册上限一概带不进去，全部由社交服按 `config/sect.json` 与库里的事实判定。
    /// 这里不算、不猜、不补默认值——拒绝就是 ok=false + reason。
    /// </summary>
    public static class SectGuild
    {
        /// <summary>四个有席位的职位编号，与 `config/sect.json` 的 positions 同形；只作请求参数。</summary>
        public const string Master = "master";
        public const string ViceMaster = "vice_master";
        public const string Elder = "elder";
        public const string Steward = "steward";
        public const string Disciple = "disciple";

        public static string CreateBody(ulong characterId, string name) =>
            "{\"character_id\":" + U(characterId)
            + ",\"name\":\"" + Escape(name ?? string.Empty) + "\"}";

        public static string JoinBody(ulong characterId, ulong sectId) =>
            "{\"character_id\":" + U(characterId) + ",\"sect_id\":" + U(sectId) + "}";

        public static string PositionBody(ulong characterId, ulong sectId, ulong targetId, string position) =>
            "{\"character_id\":" + U(characterId)
            + ",\"sect_id\":" + U(sectId)
            + ",\"target_character_id\":" + U(targetId)
            + ",\"position\":\"" + Escape(position ?? string.Empty) + "\"}";

        public static string TransferBody(ulong characterId, ulong sectId, ulong targetId) =>
            "{\"character_id\":" + U(characterId)
            + ",\"sect_id\":" + U(sectId)
            + ",\"target_character_id\":" + U(targetId) + "}";

        public static string LeaveBody(ulong characterId, ulong sectId) =>
            "{\"character_id\":" + U(characterId) + ",\"sect_id\":" + U(sectId) + "}";

        /// <summary>升级只带发起者与宗门，目标等级与消耗带不进去。</summary>
        public static string UpgradeBody(ulong characterId, ulong sectId) =>
            "{\"character_id\":" + U(characterId) + ",\"sect_id\":" + U(sectId) + "}";

        public static SectOutcome ParseSect(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return new SectOutcome { Ok = false, Reason = "bad_reply", DisplayText = "回包读不出来" };
            }

            var ok = ReadBool(json, "ok");
            var reason = Raw(json, "reason");
            var level = (int)ReadLong(json, "level");
            var cap = (int)ReadLong(json, "member_cap");
            var count = (int)ReadLong(json, "member_count");
            var name = Raw(json, "name");

            return new SectOutcome
            {
                Ok = ok,
                Reason = reason,
                SectId = (ulong)ReadLong(json, "sect_id"),
                Level = level,
                MemberCap = cap,
                MemberCount = count,
                Name = name,
                TxnId = Raw(json, "txn_id"),
                DisplayText = ok
                    ? name + " · " + level + " 级 · 名册 " + count + "/" + cap
                    : "没做成：" + reason,
            };
        }

        public static PositionOutcome ParsePosition(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return new PositionOutcome { Ok = false, Reason = "bad_reply", DisplayText = "回包读不出来" };
            }

            var ok = ReadBool(json, "ok");
            var reason = Raw(json, "reason");
            var position = Raw(json, "position");
            var cap = (int)ReadLong(json, "seat_cap");
            var count = (int)ReadLong(json, "seat_count");

            return new PositionOutcome
            {
                Ok = ok,
                Reason = reason,
                SectId = (ulong)ReadLong(json, "sect_id"),
                Position = position,
                SeatCap = cap,
                SeatCount = count,
                TxnId = Raw(json, "txn_id"),
                DisplayText = ok
                    ? position + " · 席位 " + count + "/" + cap
                    : "没做成：" + reason,
            };
        }

        public static LeadershipOutcome ParseLeadership(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return new LeadershipOutcome { Ok = false, Reason = "bad_reply", DisplayText = "回包读不出来" };
            }

            var ok = ReadBool(json, "ok");
            var reason = Raw(json, "reason");
            var oldMaster = (ulong)ReadLong(json, "old_master");
            var newMaster = (ulong)ReadLong(json, "new_master");

            return new LeadershipOutcome
            {
                Ok = ok,
                Reason = reason,
                SectId = (ulong)ReadLong(json, "sect_id"),
                OldMaster = oldMaster,
                NewMaster = newMaster,
                MasterCount = (int)ReadLong(json, "master_count"),
                ViceMasterCount = (int)ReadLong(json, "vice_master_count"),
                MemberCount = (int)ReadLong(json, "member_count"),
                TxnId = Raw(json, "txn_id"),
                DisplayText = ok
                    ? "掌门 " + oldMaster + " → " + newMaster
                    : "没做成：" + reason,
            };
        }

        public static UpgradeOutcome ParseUpgrade(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return new UpgradeOutcome { Ok = false, Reason = "bad_reply", DisplayText = "回包读不出来" };
            }

            var ok = ReadBool(json, "ok");
            var reason = Raw(json, "reason");
            var oldLevel = (int)ReadLong(json, "old_level");
            var newLevel = (int)ReadLong(json, "new_level");
            var cap = (int)ReadLong(json, "member_cap");
            var before = ReadLong(json, "construction_before");
            var after = ReadLong(json, "construction_after");
            var cost = ReadLong(json, "upgrade_cost");

            return new UpgradeOutcome
            {
                Ok = ok,
                Reason = reason,
                SectId = (ulong)ReadLong(json, "sect_id"),
                OldLevel = oldLevel,
                NewLevel = newLevel,
                MemberCap = cap,
                ConstructionBefore = before,
                ConstructionAfter = after,
                UpgradeCost = cost,
                TxnId = Raw(json, "txn_id"),
                DisplayText = ok
                    ? oldLevel + " 级 → " + newLevel + " 级 · 名册上限 " + cap
                        + " · 建设 " + before + " → " + after + "（扣 " + cost + "）"
                    : "没做成：" + reason,
            };
        }

        /// <summary>
        /// 拆名册只读回包。只取 ok / reason / sect_id / members 的角色号和职位。
        /// 人数上限、席位数字即使出现在 JSON 里也不读。
        /// </summary>
        public static RosterOutcome ParseRoster(string json)
        {
            if (string.IsNullOrEmpty(json) || Raw(json, "ok").Length == 0)
            {
                return new RosterOutcome
                {
                    Ok = false,
                    Reason = "bad_reply",
                    Members = new List<RosterMember>(),
                    DisplayText = "回包读不出来",
                };
            }

            var ok = ReadBool(json, "ok");
            var reason = Raw(json, "reason");
            var members = ParseMembers(json);
            return new RosterOutcome
            {
                Ok = ok,
                Reason = reason,
                SectId = (ulong)ReadLong(json, "sect_id"),
                Members = members,
                DisplayText = ok
                    ? "名册 " + members.Count.ToString(CultureInfo.InvariantCulture) + " 人"
                    : "没做成：" + reason,
            };
        }

        /// <summary>屏上的一行：角色号和职位原文，中间一个间隔号。不把职位翻成中文。</summary>
        public static string RosterLine(RosterMember member)
        {
            return member.CharacterId.ToString(CultureInfo.InvariantCulture)
                + " · " + (member.Position ?? string.Empty);
        }

        static List<RosterMember> ParseMembers(string json)
        {
            var rows = new List<RosterMember>();
            var start = json.IndexOf("\"members\"", StringComparison.Ordinal);
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
                rows.Add(new RosterMember
                {
                    CharacterId = (ulong)ReadLong(one, "character_id"),
                    Position = Raw(one, "position"),
                });
                at = close + 1;
            }

            return rows;
        }

        static string U(ulong value) => value.ToString(CultureInfo.InvariantCulture);

        static bool ReadBool(string json, string key) =>
            string.Equals(Raw(json, key), "true", StringComparison.OrdinalIgnoreCase);

        static long ReadLong(string json, string key)
        {
            long value;
            return long.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : 0L;
        }

        static string Raw(string json, string key) => JsonField.Of(json, key);

        static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}

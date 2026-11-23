using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>队伍里的一个人：只读三条事实 + 加入顺序（只作核对，不上屏）。</summary>
    public struct PartyMemberFactView
    {
        public ulong CharacterId;
        public bool IsLeader;
        public bool Ready;
        public long JoinedSeq;

        /// <summary>这个人是不是队长、准备好了没有。屏上只按这两条写，不自己推。</summary>
        public string StateLine => (IsLeader ? "队长" : "队员") + " · " + (Ready ? "已准备" : "未准备");
    }

    /// <summary>
    /// 队伍回包（Q7 七个 Party RPC 的 HTTP 出口）。人数上限、两个超时、队长继任、
    /// 掷点模式全部由社交服按 `config/party.json` 给事实值，这里一个都不算，也不猜。
    /// </summary>
    public struct PartyOutcome
    {
        /// <summary>回包的 <c>ok</c>。业务拒绝是 HTTP 200 + ok=false，不是网络失败。</summary>
        public bool Ok;

        /// <summary>拒绝原因原样抄回包（not_leader / party_full / invite_expired …）。</summary>
        public string Reason;

        /// <summary>在不在队伍里。false 时其余事实值一律 0 / 空。</summary>
        public bool InParty;

        public ulong PartyId;
        public ulong LeaderCharacterId;
        public int Size;
        public int MaxSize;
        public bool AllReady;
        public long ReadyDeadlineMs;

        /// <summary>这一队此刻的分配口径（personal / need_greed），只用来显示。</summary>
        public string LootMode;

        /// <summary>掷点方式（none / server）。客户端不掷，只写「谁掷」。</summary>
        public string LootRoll;

        public int RollMin;
        public int RollMax;

        public List<PartyMemberFactView> Members;

        public static PartyOutcome Failure(string reason) => new PartyOutcome
        {
            Ok = false,
            Reason = reason ?? string.Empty,
            LootMode = string.Empty,
            LootRoll = string.Empty,
            Members = new List<PartyMemberFactView>(),
        };
    }

    /// <summary>
    /// 七条队伍口的路径、请求体与回包拆装。客户端带不进人数上限 / 队长 / 截止时刻，
    /// 也不带事务号——网关生成。请求体只有角色号与目标角色号。
    /// </summary>
    public static class CharacterParty
    {
        public const string DescribePath = "/party";
        public const string InvitePath = "/party/invite";
        public const string RespondPath = "/party/respond";
        public const string ReadyStartPath = "/party/ready/start";
        public const string ReadyPath = "/party/ready";
        public const string LeavePath = "/party/leave";
        public const string DisbandPath = "/party/disband";

        /// <summary>只读口：只带角色号，令牌在请求头上。</summary>
        public static string DescribeQuery(ulong characterId) =>
            DescribePath + "?character_id=" + characterId.ToString(CultureInfo.InvariantCulture);

        /// <summary>邀请：自己 + 目标。目标是谁由玩家填，服务端复核。</summary>
        public static string InviteBody(ulong characterId, ulong targetCharacterId) =>
            "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
            + ",\"target_character_id\":" + targetCharacterId.ToString(CultureInfo.InvariantCulture) + "}";

        /// <summary>接受 / 拒绝：自己 + 发起邀请的人 + 收不收。</summary>
        public static string RespondBody(ulong characterId, ulong fromCharacterId, bool accept) =>
            "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
            + ",\"from_character_id\":" + fromCharacterId.ToString(CultureInfo.InvariantCulture)
            + ",\"accept\":" + (accept ? "true" : "false") + "}";

        /// <summary>准备态：自己 + 准备好了没有。</summary>
        public static string ReadyBody(ulong characterId, bool ready) =>
            "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
            + ",\"ready\":" + (ready ? "true" : "false") + "}";

        /// <summary>发起准备 / 离队 / 解散：只有自己。</summary>
        public static string MemberBody(ulong characterId) =>
            "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture) + "}";

        public static PartyOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return PartyOutcome.Failure("bad_reply");
            }

            return new PartyOutcome
            {
                Ok = string.Equals(JsonField.Of(json, "ok"), "true", StringComparison.OrdinalIgnoreCase),
                Reason = JsonField.Of(json, "reason"),
                InParty = ReadBool(json, "in_party"),
                PartyId = ReadUlong(json, "party_id"),
                LeaderCharacterId = ReadUlong(json, "leader_character_id"),
                Size = ReadInt(json, "size"),
                MaxSize = ReadInt(json, "max_size"),
                AllReady = ReadBool(json, "all_ready"),
                ReadyDeadlineMs = ReadLong(json, "ready_deadline_ms"),
                LootMode = JsonField.Of(json, "loot_mode"),
                LootRoll = JsonField.Of(json, "loot_roll"),
                RollMin = ReadInt(json, "roll_min"),
                RollMax = ReadInt(json, "roll_max"),
                Members = ReadMembers(json),
            };
        }

        /// <summary>
        /// 队伍那一句。不在队伍就照回包写「不在队伍」，人数与上限只用回包的两个数。
        /// </summary>
        public static string HeadLine(bool requested, PartyOutcome outcome)
        {
            if (!requested)
            {
                return "队伍名单还没到";
            }

            if (!outcome.Ok && !outcome.InParty)
            {
                return outcome.Reason.Length == 0 ? "不在队伍" : "不在队伍：" + outcome.Reason;
            }

            if (!outcome.InParty)
            {
                return "不在队伍";
            }

            return "队伍 " + outcome.PartyId + " · " + outcome.Size + " / " + outcome.MaxSize + " 人"
                + (outcome.AllReady ? " · 全队已准备" : string.Empty);
        }

        /// <summary>
        /// 掷点模式那一句。personal = 每人各发整份；need_greed = 全队一份、服务端掷点。
        /// 回包里没有这一格就写「回包没给分配口径」，不拿配置里的默认值顶上去。
        /// </summary>
        public static string LootLine(PartyOutcome outcome)
        {
            if (outcome.LootMode.Length == 0)
            {
                return "回包没给分配口径";
            }

            if (outcome.LootMode == "personal")
            {
                return "每人各发整份 · 不掷点";
            }

            if (outcome.LootMode == "need_greed")
            {
                var who = outcome.LootRoll == "server" ? "服务端掷点" : "掷点：" + outcome.LootRoll;
                return "全队一份 · " + who
                    + (outcome.RollMax >= outcome.RollMin && outcome.RollMax > 0
                        ? "（" + outcome.RollMin + "–" + outcome.RollMax + "）"
                        : string.Empty);
            }

            return "分配口径：" + outcome.LootMode;
        }

        /// <summary>`"members":[{…},{…}]` 那一段拆成一行一行。形状固定，不引序列化器。</summary>
        static List<PartyMemberFactView> ReadMembers(string json)
        {
            var rows = new List<PartyMemberFactView>();
            var key = json.IndexOf("\"members\"", StringComparison.Ordinal);
            if (key < 0)
            {
                return rows;
            }

            var open = json.IndexOf('[', key);
            var close = open < 0 ? -1 : json.IndexOf(']', open);
            if (open < 0 || close < 0 || close <= open)
            {
                return rows;
            }

            var body = json.Substring(open + 1, close - open - 1);
            var index = 0;
            while (index < body.Length)
            {
                var start = body.IndexOf('{', index);
                if (start < 0)
                {
                    break;
                }

                var end = body.IndexOf('}', start);
                if (end < 0)
                {
                    break;
                }

                var one = body.Substring(start, end - start + 1);
                rows.Add(new PartyMemberFactView
                {
                    CharacterId = ReadUlong(one, "character_id"),
                    IsLeader = ReadBool(one, "is_leader"),
                    Ready = ReadBool(one, "ready"),
                    JoinedSeq = ReadLong(one, "joined_seq"),
                });

                index = end + 1;
            }

            return rows;
        }

        static bool ReadBool(string json, string key) =>
            string.Equals(JsonField.Of(json, key), "true", StringComparison.OrdinalIgnoreCase);

        static int ReadInt(string json, string key)
        {
            int value;
            return int.TryParse(JsonField.Of(json, key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value)
                ? value
                : 0;
        }

        static long ReadLong(string json, string key)
        {
            long value;
            return long.TryParse(JsonField.Of(json, key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value)
                ? value
                : 0L;
        }

        static ulong ReadUlong(string json, string key)
        {
            ulong value;
            return ulong.TryParse(JsonField.Of(json, key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value)
                ? value
                : 0UL;
        }
    }
}

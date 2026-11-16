using System;
using System.Globalization;
using System.Text;

namespace Mmo.Net
{
    /// <summary>
    /// 竞技场 / 战场进场回包（N5 的 `POST /versus/arena/enter` 与 `POST /versus/battlefield/enter`）。
    ///
    /// 回包只给「该往哪连、按什么模式开」：进场路径 `battle_path`、模式 `arena_mode` 与
    /// `party_size`、队伍编号 `team`、场次 `battlefield`，以及角色服给的职业与分支。
    /// 战力、平衡系数、匹配分组都不在这里——那些是战斗服按 P2-3a / P2-3b 开战时的事，
    /// 客户端不换算、不补算。
    /// </summary>
    public struct VersusOutcome
    {
        public bool Ok;
        public string Reason;

        /// <summary>该往哪连（回包原样，目前是 `/battle`）。</summary>
        public string BattlePath;

        public ulong CharacterId;
        public string ClassId;
        public string BranchId;

        /// <summary>竞技场模式编号与这一场的人数（回包给，客户端不按 1v1 / 3v3 自己乘）。</summary>
        public string ArenaMode;
        public int PartySize;

        /// <summary>这一场竞技场的场次号；回包没给就是空串。</summary>
        public string Party;

        /// <summary>战场场次与队伍编号（`a` / `b`）。</summary>
        public string Match;
        public string Team;
        public string Battlefield;

        public static VersusOutcome Failure(string reason) => new VersusOutcome
        {
            Ok = false,
            Reason = reason ?? string.Empty,
            BattlePath = string.Empty,
            ClassId = string.Empty,
            BranchId = string.Empty,
            ArenaMode = string.Empty,
            Party = string.Empty,
            Match = string.Empty,
            Team = string.Empty,
            Battlefield = string.Empty,
        };
    }

    /// <summary>
    /// 两条进场口的路径、请求体与回包拆装。两条口形状相同（都是平铺的一段 JSON），
    /// 所以只写一个 `Parse`；哪条口该带哪些字段由请求体决定，不在这里猜。
    /// </summary>
    public static class VersusEntry
    {
        public const string ArenaPath = "/versus/arena/enter";

        public const string BattlefieldPath = "/versus/battlefield/enter";

        /// <summary>
        /// 竞技场请求体。只带角色号与模式编号；职业、分支、人数都由网关去角色服取，
        /// 客户端塞进去也没人认。
        /// </summary>
        public static string ArenaBody(ulong characterId, string arenaMode)
        {
            var sb = new StringBuilder(96);
            sb.Append("{\"character_id\":").Append(characterId.ToString(CultureInfo.InvariantCulture))
                .Append(",\"arena_mode\":\"").Append(Escape(arenaMode)).Append("\"}");
            return sb.ToString();
        }

        /// <summary>
        /// 战场请求体。`match` 是这一场的场次号、`team` 是 a / b，两个都是请求参数：
        /// 能不能进、算不算同队由网关与战斗服判，客户端不组队、不排队。
        /// </summary>
        public static string BattlefieldBody(ulong characterId, string match, string team)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"character_id\":").Append(characterId.ToString(CultureInfo.InvariantCulture))
                .Append(",\"match\":\"").Append(Escape(match))
                .Append("\",\"team\":\"").Append(Escape(team)).Append("\"}");
            return sb.ToString();
        }

        public static VersusOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return VersusOutcome.Failure("bad_reply");
            }

            var ok = string.Equals(JsonField.Of(json, "ok"), "true", StringComparison.OrdinalIgnoreCase);

            return new VersusOutcome
            {
                Ok = ok,
                Reason = Unescape(JsonField.Of(json, "reason")),
                BattlePath = Unescape(JsonField.Of(json, "battle_path")),
                CharacterId = ReadUlong(json, "character_id"),
                ClassId = Unescape(JsonField.Of(json, "class_id")),
                BranchId = Unescape(JsonField.Of(json, "branch_id")),
                ArenaMode = Unescape(JsonField.Of(json, "arena_mode")),
                PartySize = ReadInt(json, "party_size"),
                Party = Unescape(JsonField.Of(json, "party")),
                Match = Unescape(JsonField.Of(json, "match")),
                Team = Unescape(JsonField.Of(json, "team")),
                Battlefield = Unescape(JsonField.Of(json, "battlefield")),
            };
        }

        /// <summary>
        /// 一行回包事实。只照抄回包：进场路径、模式与人数、场次与队伍、职业与分支。
        /// 回包里没有的字段写「回包没带」，不拿上一份或默认值顶上。
        /// </summary>
        public static string SummaryLine(VersusOutcome outcome)
        {
            if (!outcome.Ok)
            {
                return "服务端：" + (outcome.Reason.Length == 0 ? "没成（回包没带原因）" : outcome.Reason);
            }

            var sb = new StringBuilder(160);
            sb.Append("进场路径 ").Append(Blank(outcome.BattlePath));
            if (outcome.ArenaMode.Length > 0)
            {
                sb.Append(" · 模式 ").Append(outcome.ArenaMode)
                    .Append(" · 人数 ").Append(outcome.PartySize.ToString(CultureInfo.InvariantCulture))
                    .Append(" · 场次 ").Append(Blank(outcome.Party));
            }
            else
            {
                sb.Append(" · 场次 ").Append(Blank(outcome.Battlefield))
                    .Append(" · 队伍 ").Append(Blank(outcome.Team));
            }

            sb.Append(" · 职业 ").Append(Blank(outcome.ClassId))
                .Append(" · 分支 ").Append(outcome.BranchId.Length == 0 ? "无" : outcome.BranchId);
            return sb.ToString();
        }

        /// <summary>
        /// 一行「这一场是哪套算法」。模式编号只作对照：回包给 `duel` / `trio` 时指到竞技场，
        /// 给场次时指到战场；两者都没有就照抄回包、不猜一个。
        /// </summary>
        public static string ModeLine(VersusOutcome outcome)
        {
            if (!outcome.Ok)
            {
                return "还没有进场回包";
            }

            if (outcome.ArenaMode.Length > 0)
            {
                return "竞技场 · 属性模板统一（换装不改模板属性）";
            }

            if (outcome.Battlefield.Length > 0)
            {
                return "战场 · 装备生效，同队战力由战斗服压";
            }

            return "回包没带模式字段 · 不猜";
        }

        static string Blank(string value) => string.IsNullOrEmpty(value) ? "（回包没带）" : value;

        static int ReadInt(string json, string key)
        {
            int value;
            return int.TryParse(JsonField.Of(json, key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value)
                ? value
                : 0;
        }

        static ulong ReadUlong(string json, string key)
        {
            ulong value;
            return ulong.TryParse(JsonField.Of(json, key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value)
                ? value
                : 0UL;
        }

        /// <summary>请求体里的字符串：只挡掉会破坏 JSON 的两个字符，编号与场次都是 ASCII。</summary>
        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>解 JSON 的转义（网关把非 ASCII 写成 `\uXXXX`）。</summary>
        static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0)
            {
                return value ?? string.Empty;
            }

            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length)
                {
                    sb.Append(value[i]);
                    continue;
                }

                var next = value[i + 1];
                if (next == 'u' && i + 5 < value.Length)
                {
                    var hex = value.Substring(i + 2, 4);
                    int code;
                    if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                    {
                        sb.Append((char)code);
                        i += 5;
                        continue;
                    }
                }

                switch (next)
                {
                    case 'n':
                        sb.Append('\n');
                        i++;
                        continue;
                    case 't':
                        sb.Append('\t');
                        i++;
                        continue;
                    case 'r':
                        sb.Append('\r');
                        i++;
                        continue;
                    case '"':
                        sb.Append('"');
                        i++;
                        continue;
                    case '\\':
                        sb.Append('\\');
                        i++;
                        continue;
                    case '/':
                        sb.Append('/');
                        i++;
                        continue;
                }

                sb.Append(next);
                i++;
            }

            return sb.ToString();
        }
    }
}

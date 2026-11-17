using System;
using System.Globalization;
using System.Text;

namespace Mmo.Net
{
    /// <summary>
    /// 境界段内阶回包（N13 的 `GET /realm/phase` 与 `POST /realm/phase/promote`）。
    ///
    /// 段内阶名、被动万分比、升一阶要几级，全由角色服按境界配置算好再回来；
    /// 客户端不认阶名表、不按等级推「能不能升」、不把被动乘进任何伤害。
    /// 被拒时回包给什么就显示什么（门槛 <see cref="LevelRequired"/> 也照抄）。
    /// </summary>
    public struct RealmPhaseOutcome
    {
        /// <summary>回包的 <c>ok</c>。业务拒绝是 HTTP 200 + ok=false，不是网络失败。</summary>
        public bool Ok;

        /// <summary>拒绝原因原样抄回包（`level_not_enough` / `realm_phase_max`）。</summary>
        public string Reason;

        public ulong CharacterId;

        /// <summary>大境界规范名（回包 <c>realm</c>），展示名仍走 `/inventory` 的 `realm_display`。</summary>
        public string Realm;

        /// <summary>段内阶序号（回包 <c>realm_phase</c>），只读，不参与本地判定。</summary>
        public int Phase;

        /// <summary>段内阶名（回包 <c>phase_name</c>），叫什么全看角色服那份境界配置，客户端不认名。</summary>
        public string PhaseName;

        /// <summary>这一阶的被动万分比（回包 <c>passive_pct_bps</c>），客户端只显示不乘。</summary>
        public int PassivePctBps;

        public int FromPhase;

        public int ToPhase;

        /// <summary>升阶口回包里的当前等级；只读口没有这一格，解析成 0。</summary>
        public int Level;

        /// <summary>升阶口回包里的门槛等级；被拒时照着它写「还差几级」，不自己算。</summary>
        public int LevelRequired;

        public bool Duplicate;

        public string DisplayText;

        public static RealmPhaseOutcome Failure(string reason) => new RealmPhaseOutcome
        {
            Ok = false,
            Reason = reason ?? string.Empty,
            Realm = string.Empty,
            PhaseName = string.Empty,
            DisplayText = "没读到：" + (reason ?? string.Empty),
        };
    }

    /// <summary>
    /// 两条境界子阶口的路径与回包拆装。只读口不带请求体，升阶口只有角色号——
    /// 事务号由网关生成，客户端带不进任何数值。
    /// </summary>
    public static class CharacterRealmPhase
    {
        public const string ReadPath = "/realm/phase";

        public const string PromotePath = "/realm/phase/promote";

        /// <summary>只读口：只带角色号，令牌在请求头上。</summary>
        public static string ReadQuery(ulong characterId) =>
            ReadPath + "?character_id=" + characterId.ToString(CultureInfo.InvariantCulture);

        /// <summary>升阶口请求体：只有角色号，没有事务号、没有目标阶。</summary>
        public static string PromoteBody(ulong characterId) =>
            "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture) + "}";

        public static RealmPhaseOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return RealmPhaseOutcome.Failure("bad_reply");
            }

            var ok = string.Equals(JsonField.Of(json, "ok"), "true", StringComparison.OrdinalIgnoreCase);
            var reason = JsonField.Of(json, "reason");
            var phaseName = Unescape(JsonField.Of(json, "phase_name"));

            return new RealmPhaseOutcome
            {
                Ok = ok,
                Reason = reason,
                CharacterId = ReadUlong(json, "character_id"),
                Realm = Unescape(JsonField.Of(json, "realm")),
                Phase = ReadInt(json, "realm_phase"),
                PhaseName = phaseName,
                PassivePctBps = ReadInt(json, "passive_pct_bps"),
                FromPhase = ReadInt(json, "from_phase"),
                ToPhase = ReadInt(json, "to_phase"),
                Level = ReadInt(json, "level"),
                LevelRequired = ReadInt(json, "level_required"),
                Duplicate = string.Equals(JsonField.Of(json, "duplicate"), "true",
                    StringComparison.OrdinalIgnoreCase),
                DisplayText = ok
                    ? phaseName + " · 被动 " + ReadInt(json, "passive_pct_bps") + " bps"
                    : ("没升成：" + (reason.Length == 0 ? "未知原因" : reason)),
            };
        }

        /// <summary>
        /// 「段内阶」那一行的值。还没请求就写「尚未请求」，没读到就写原因；
        /// 阶名与万分比一律抄回包，不拿配置里的数补上。
        /// </summary>
        public static string PhaseLine(bool requested, RealmPhaseOutcome outcome)
        {
            if (!requested)
            {
                return "尚未请求";
            }

            if (!outcome.Ok)
            {
                return "没读到：" + (outcome.Reason.Length == 0 ? "未知原因" : outcome.Reason);
            }

            return outcome.PhaseName + " · 被动 " + outcome.PassivePctBps + " bps";
        }

        /// <summary>升阶结果那一行：成不成、从第几阶到第几阶、门槛由回包给。</summary>
        public static string PromoteLine(bool requested, RealmPhaseOutcome outcome)
        {
            if (!requested)
            {
                return "尚未请求";
            }

            if (!outcome.Ok)
            {
                return "被拒：" + (outcome.Reason.Length == 0 ? "未知原因" : outcome.Reason);
            }

            return outcome.PhaseName + "（第 " + outcome.FromPhase + " → " + outcome.ToPhase + " 阶）";
        }

        /// <summary>被拒时那一行门槛说明。等级照抄回包，不够就写「服务端要 N 级」，不自己比。</summary>
        public static string RequiredLine(bool requested, RealmPhaseOutcome outcome)
        {
            if (!requested)
            {
                return "尚未请求";
            }

            if (outcome.Ok)
            {
                return "回包等级 " + outcome.Level + " · 已达门槛";
            }

            if (outcome.LevelRequired <= 0)
            {
                return "门槛由服务端给";
            }

            return "回包等级 " + outcome.Level + " · 这一阶要 " + outcome.LevelRequired + " 级";
        }

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

        /// <summary>
        /// 解 JSON 转义。网关把中文写成 `\u4E00\u9636` 这种形态发出来，
        /// 不解开的话屏上显示的是反斜杠加十六进制，看着像阶名没进来。
        /// </summary>
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

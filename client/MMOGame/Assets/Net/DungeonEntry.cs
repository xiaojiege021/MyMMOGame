using System;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>
    /// 进本回包的几类结果。界面按这个分口径，不去看 HTTP 数字、也不自己判能不能进。
    /// </summary>
    public enum EnterKind
    {
        /// <summary>服务端说能进。只有这一档才往下走。</summary>
        Allowed,

        /// <summary>200 但 allowed=false：疲劳、周份额、人数或等级被拒，原因在 reason 里。</summary>
        Refused,

        /// <summary>400：请求没发出去（缺字段、人数不是数）。</summary>
        BadRequest,

        /// <summary>401：令牌失效，回登录，不弹「进不去」。</summary>
        InvalidToken,

        /// <summary>503：服务端暂时连不上库。</summary>
        Unavailable,

        /// <summary>请求没到网关（不是 HTTP 状态）。</summary>
        Network,

        /// <summary>其它状态码。只把服务端的 error 原样带出来。</summary>
        Rejected,
    }

    /// <summary>
    /// 进本结果。疲劳、周份额、每人份数全是回包里的数，客户端一个都不算。
    /// 界面上的「还剩多少」也只用这里的字段，不拿本地上次的值去减。
    /// </summary>
    public struct EnterOutcome
    {
        public EnterKind Kind;

        /// <summary>被拒原因，例如 fatigue_exhausted / share_exhausted / party_size_mismatch。</summary>
        public string Reason;

        /// <summary>这张副本表允许的人数上限（回包 max_players）。</summary>
        public int MaxPlayers;

        /// <summary>进本之后的疲劳值。</summary>
        public long Fatigue;

        /// <summary>这一次扣掉的疲劳。</summary>
        public long FatigueCost;

        /// <summary>每天疲劳上限。</summary>
        public long FatigueDailyCap;

        /// <summary>这一次占掉的周份额。</summary>
        public int WeeklyShareCost;

        /// <summary>本周还剩多少个单人份。</summary>
        public int WeeklyShareRemaining;

        /// <summary>每周份额总量。</summary>
        public int WeeklyShareCap;

        /// <summary>服务端按人数算出的份数，界面的「每人 N 份」就是它。</summary>
        public int RewardShares;

        /// <summary>这次进本在角色服写下的记录编号。</summary>
        public string EntryId;

        public string Error;

        public bool Allowed => Kind == EnterKind.Allowed;
    }

    /// <summary>
    /// 进本这一件事的请求与回包拆装：`POST /dungeon/enter`（方案 10.1、开发架构第 14 节第 25–28 行）。
    ///
    /// 客户端一条判定都不做：能不能进、扣多少疲劳、占多少周份额、每人几份，
    /// 全等服务端说。请求里只有角色号、副本编号和进本人数——
    /// 人数是「这一场几个人」，不是产出，也不是扣除，产出与扣除都由服务端乘。
    ///
    /// `Mmo.Net` 是 `noEngineReferences` 的纯 .NET 程序集，所以这里手写固定形状的
    /// JSON，不引引擎的序列化器。
    /// </summary>
    public static class DungeonEntry
    {
        /// <summary>
        /// 进本请求。<paramref name="dungeonId"/> 留空表示主线（网关按 main 处理）；
        /// 人数必须是这一场的真实人数，战斗服那边 query 的 party_size 要与它同一个数。
        /// </summary>
        public static string EnterBody(ulong characterId, string dungeonId, int partySize)
        {
            var id = partySize < 1 ? 1 : partySize;
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"dungeon_id\":" + Quote(dungeonId ?? string.Empty)
                + ",\"party_size\":" + id.ToString(CultureInfo.InvariantCulture) + "}";
        }

        /// <summary>
        /// 进本回包。<paramref name="status"/> 为 0 表示请求没到网关（连接被拒、超时）。
        /// 200 且 allowed=true 才算能进；200 但 allowed=false 是业务拒绝，原因照 reason 显示。
        /// </summary>
        public static EnterOutcome ParseEnter(int status, string body)
        {
            body = body ?? string.Empty;

            if (status == 0)
            {
                return new EnterOutcome { Kind = EnterKind.Network, Error = "network" };
            }

            if (status == 200)
            {
                var allowed = ReadBool(body, "allowed");
                var outcome = new EnterOutcome
                {
                    Kind = allowed ? EnterKind.Allowed : EnterKind.Refused,
                    Reason = Raw(body, "reason"),
                    MaxPlayers = ReadInt(body, "max_players"),
                    Fatigue = ReadLong(body, "fatigue"),
                    FatigueCost = ReadLong(body, "fatigue_cost"),
                    FatigueDailyCap = ReadLong(body, "fatigue_daily_cap"),
                    WeeklyShareCost = ReadInt(body, "weekly_share_cost"),
                    WeeklyShareRemaining = ReadInt(body, "weekly_share_remaining"),
                    WeeklyShareCap = ReadInt(body, "weekly_share_cap"),
                    RewardShares = ReadInt(body, "reward_shares"),
                    EntryId = Raw(body, "entry_id"),
                };

                if (!allowed && outcome.Reason.Length == 0)
                {
                    // 拒绝了却没给原因：也照拒绝算，不自己编一条「应该能进」。
                    outcome.Reason = "refused";
                }

                return outcome;
            }

            return new EnterOutcome { Kind = KindOf(status), Error = ErrorOf(body, status) };
        }

        /// <summary>结果名。日志与核对都用它，别在别处再拼一套字符串。</summary>
        public static string KindName(EnterKind kind)
        {
            switch (kind)
            {
                case EnterKind.Allowed:
                    return "allowed";
                case EnterKind.Refused:
                    return "refused";
                case EnterKind.BadRequest:
                    return "bad_request";
                case EnterKind.InvalidToken:
                    return "invalid_token";
                case EnterKind.Unavailable:
                    return "unavailable";
                case EnterKind.Network:
                    return "network";
                default:
                    return "rejected";
            }
        }

        // ------------------------------------------------------------------ 内部

        static EnterKind KindOf(int status)
        {
            switch (status)
            {
                case 400:
                    return EnterKind.BadRequest;
                case 401:
                    return EnterKind.InvalidToken;
                case 503:
                    return EnterKind.Unavailable;
                default:
                    return EnterKind.Rejected;
            }
        }

        static string ErrorOf(string body, int status)
        {
            var error = Raw(body, "error");
            return error.Length > 0 ? error : "http_" + status.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 取一个字符串字段。只认「字段名后紧跟冒号」，所以 `weekly_share_remaining`
        /// 不会被 `weekly_share` 抢走，也不会把别的字段的后半截当自己的值。
        /// </summary>
        static string Raw(string body, string key)
        {
            var at = 0;
            while (true)
            {
                var index = body.IndexOf("\"" + key + "\"", at, StringComparison.Ordinal);
                if (index < 0)
                {
                    return string.Empty;
                }

                var p = index + key.Length + 2;
                while (p < body.Length && (body[p] == ' ' || body[p] == '\t'))
                {
                    p++;
                }

                if (p >= body.Length || body[p] != ':')
                {
                    at = index + key.Length + 2;
                    continue;
                }

                p++;
                while (p < body.Length && (body[p] == ' ' || body[p] == '\t'))
                {
                    p++;
                }

                if (p >= body.Length)
                {
                    return string.Empty;
                }

                if (body[p] == '"')
                {
                    var end = body.IndexOf('"', p + 1);
                    return end < 0 ? string.Empty : body.Substring(p + 1, end - p - 1);
                }

                var stop = p;
                while (stop < body.Length && body[stop] != ',' && body[stop] != '}' && body[stop] != ' ')
                {
                    stop++;
                }

                return body.Substring(p, stop - p);
            }
        }

        static bool ReadBool(string body, string key)
        {
            return string.Equals(Raw(body, key), "true", StringComparison.OrdinalIgnoreCase);
        }

        static long ReadLong(string body, string key)
        {
            long.TryParse(Raw(body, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        static int ReadInt(string body, string key)
        {
            int.TryParse(Raw(body, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        static string Quote(string value)
        {
            var text = value ?? string.Empty;
            var builder = new System.Text.StringBuilder(text.Length + 2);
            builder.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }
}

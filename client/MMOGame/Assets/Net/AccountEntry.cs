using System;
using System.Globalization;
using System.Text;

namespace Mmo.Net
{
    /// <summary>
    /// 入口回包的四类结果。界面按这个分口径，不去看 HTTP 数字、也不自己判口令对不对。
    /// </summary>
    public enum EntryKind
    {
        /// <summary>服务端说成了。只有这一档能往下走。</summary>
        Ok,

        /// <summary>400：请求没发出去。</summary>
        BadRequest,

        /// <summary>401 且是登录：账号或口令不对，不区分是哪个。</summary>
        InvalidCredentials,

        /// <summary>401 且是建角：令牌失效，回登录，不弹「口令错」。</summary>
        InvalidToken,

        /// <summary>409：名号已被占用。</summary>
        NameTaken,

        /// <summary>503：服务端暂时连不上库。</summary>
        Unavailable,

        /// <summary>请求没到网关（不是 HTTP 状态）。</summary>
        Network,

        /// <summary>其它状态码。只把服务端的 error 原样带出来。</summary>
        Rejected,
    }

    /// <summary>登录结果。令牌只有服务端给的那一个，本地不造。</summary>
    public struct LoginOutcome
    {
        public EntryKind Kind;
        public string Token;
        public int ExpiresInS;
        public string Error;

        public bool Ok => Kind == EntryKind.Ok && !string.IsNullOrEmpty(Token);
    }

    /// <summary>建角结果。角色号、职业、等级都是回包里的值。</summary>
    public struct CreateOutcome
    {
        public EntryKind Kind;
        public ulong CharacterId;
        public string Name;
        public string Race;
        public string ClassId;
        public int Level;
        public string Realm;
        public string Error;

        public bool Ok => Kind == EntryKind.Ok && CharacterId != 0;
    }

    /// <summary>
    /// 入口两件事的请求与回包拆装：`POST /login` 与 `POST /character`（方案 2.2、8.4）。
    ///
    /// 客户端一条判定都不做：口令对不对、名号能不能用、出身职业认不认，全等服务端说。
    /// 请求里也不带任何可以左右结果的东西——没有令牌、没有等级、没有属性；
    /// 建角的分支留空（15 级一转才选），外观按出身表给。
    ///
    /// `Mmo.Net` 是 `noEngineReferences` 的纯 .NET 程序集，所以这里手写固定形状的
    /// JSON，不引引擎的序列化器。
    /// </summary>
    public static class AccountEntry
    {
        /// <summary>登录请求。只有账号与口令。</summary>
        public static string LoginBody(string account, string password)
        {
            return "{\"account\":" + Quote(account ?? string.Empty)
                + ",\"password\":" + Quote(password ?? string.Empty) + "}";
        }

        /// <summary>
        /// 建角请求。分支固定为空串（创角不列分支），外观是出身表里那一段 JSON，
        /// 由服务端原样落库；装备、等级、境界、阵营一律不发。
        /// </summary>
        public static string CreateBody(string name, string race, string appearanceJson, string classId)
        {
            return "{\"name\":" + Quote(name ?? string.Empty)
                + ",\"race\":" + Quote(race ?? string.Empty)
                + ",\"appearance\":" + Quote(string.IsNullOrEmpty(appearanceJson) ? "{}" : appearanceJson)
                + ",\"class\":" + Quote(classId ?? string.Empty)
                + ",\"branch\":\"\"}";
        }

        /// <summary>
        /// 登录回包。<paramref name="status"/> 为 0 表示请求没到网关（连接被拒、超时）。
        /// </summary>
        public static LoginOutcome ParseLogin(int status, string body)
        {
            body = body ?? string.Empty;

            if (status == 0)
            {
                return new LoginOutcome { Kind = EntryKind.Network, Error = "network" };
            }

            if (status == 200)
            {
                var token = Raw(body, "token");
                if (token.Length == 0)
                {
                    // 200 但没令牌：仍然按没成算，绝不拿本地造一个顶上。
                    return new LoginOutcome { Kind = EntryKind.Rejected, Error = "token_missing" };
                }

                return new LoginOutcome
                {
                    Kind = EntryKind.Ok,
                    Token = token,
                    ExpiresInS = ReadInt(body, "expires_in_s"),
                };
            }

            return new LoginOutcome { Kind = KindOf(status, false), Error = ErrorOf(body, status) };
        }

        /// <summary>建角回包。</summary>
        public static CreateOutcome ParseCreate(int status, string body)
        {
            body = body ?? string.Empty;

            if (status == 0)
            {
                return new CreateOutcome { Kind = EntryKind.Network, Error = "network" };
            }

            if (status == 200 || status == 201)
            {
                var id = Raw(body, "character_id");
                if (id.Length == 0)
                {
                    return new CreateOutcome { Kind = EntryKind.Rejected, Error = "character_id_missing" };
                }

                long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed);
                return new CreateOutcome
                {
                    Kind = EntryKind.Ok,
                    CharacterId = (ulong)Math.Max(0L, parsed),
                    Name = Raw(body, "name"),
                    Race = Raw(body, "race"),
                    ClassId = Raw(body, "class"),
                    Level = ReadInt(body, "level"),
                    Realm = Raw(body, "realm"),
                };
            }

            return new CreateOutcome { Kind = KindOf(status, true), Error = ErrorOf(body, status) };
        }

        /// <summary>结果名。日志与核对都用它，别在别处再拼一套字符串。</summary>
        public static string KindName(EntryKind kind)
        {
            switch (kind)
            {
                case EntryKind.Ok:
                    return "ok";
                case EntryKind.BadRequest:
                    return "bad_request";
                case EntryKind.InvalidCredentials:
                    return "invalid_credentials";
                case EntryKind.InvalidToken:
                    return "invalid_token";
                case EntryKind.NameTaken:
                    return "name_taken";
                case EntryKind.Unavailable:
                    return "unavailable";
                case EntryKind.Network:
                    return "network";
                default:
                    return "rejected";
            }
        }

        // ------------------------------------------------------------------ 内部

        /// <summary>状态码到底哪一档。建角与登录的 401 含义不同，所以分开。</summary>
        static EntryKind KindOf(int status, bool creating)
        {
            switch (status)
            {
                case 400:
                    return EntryKind.BadRequest;
                case 401:
                    return creating ? EntryKind.InvalidToken : EntryKind.InvalidCredentials;
                case 409:
                    return EntryKind.NameTaken;
                case 503:
                    return EntryKind.Unavailable;
                default:
                    return EntryKind.Rejected;
            }
        }

        /// <summary>错误字段。回包没带就按状态码给一个可读的兜底。</summary>
        static string ErrorOf(string body, int status)
        {
            var error = Raw(body, "error");
            return error.Length > 0 ? error : "http_" + status.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>手写 JSON 的字符串转义。账号口令与名号都可能带引号。</summary>
        static string Quote(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
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
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        static int ReadInt(string json, string key)
        {
            int.TryParse(Raw(json, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        /// <summary>
        /// 取一个顶层字段的原始文本。字段固定，不做通用解析。
        /// 只认「字段名后面紧跟冒号」，免得某个字符串值恰好等于另一个字段名时读错。
        /// </summary>
        static string Raw(string json, string key)
        {
            var needle = "\"" + key + "\"";
            var at = -1;
            var scan = 0;
            while (true)
            {
                var hit = json.IndexOf(needle, scan, StringComparison.Ordinal);
                if (hit < 0)
                {
                    return string.Empty;
                }

                var after = hit + needle.Length;
                var p = after;
                while (p < json.Length && char.IsWhiteSpace(json[p]))
                {
                    p++;
                }

                if (p < json.Length && json[p] == ':')
                {
                    at = hit;
                    break;
                }

                scan = after;
            }

            var colon = json.IndexOf(':', at + needle.Length);
            if (colon < 0)
            {
                return string.Empty;
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
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Gateway;

/// <summary>
/// 会话令牌放在网关进程内存里（在线状态按技术架构第 3 节放对应进程内存，不另装库）。
/// 令牌本身是 32 字节随机数，进程一停就全失效，重启后要重新登录。
/// P1-1a1 只发；P1-1a2 起由网关校验：连接握手时验一次，之后每帧再确认没过期。
/// </summary>
public sealed class TokenStore
{
    sealed record Entry(string Account, DateTimeOffset ExpiresAt);

    readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);

    public int Count => _tokens.Count;

    public (string Token, DateTimeOffset ExpiresAt) Issue(string account, int ttlSeconds)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(ttlSeconds);
        _tokens[token] = new Entry(account, expiresAt);
        return (token, expiresAt);
    }

    /// <summary>
    /// 认一个令牌。空串、没发过的、已过期的一律不认；过期的顺手从表里删掉。
    /// 时间用调用方给的 now，核对脚本可以自己造过期现场。
    /// </summary>
    public bool TryResolve(string? token, DateTimeOffset now, out string account, out DateTimeOffset expiresAt)
    {
        account = "";
        expiresAt = default;

        if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(token, out var entry))
        {
            return false;
        }

        if (now >= entry.ExpiresAt)
        {
            _tokens.TryRemove(token, out _);
            return false;
        }

        account = entry.Account;
        expiresAt = entry.ExpiresAt;
        return true;
    }

    static string Base64Url(byte[] raw)
    {
        var text = Convert.ToBase64String(raw).TrimEnd('=');
        return text.Replace('+', '-').Replace('/', '_');
    }

    public override string ToString()
    {
        // 日志里只打数量，不打令牌。
        var sb = new StringBuilder();
        sb.Append("tokens=").Append(_tokens.Count);
        return sb.ToString();
    }
}

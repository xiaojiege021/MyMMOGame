using Grpc.Core;
using Mmo.Proto.Character;

namespace Gateway;

/// <summary>
/// 网关的鉴权与归属复核只在这里写一份（D10）。此前 20 份
/// <c>Authorize</c> / <c>TryAuthorize</c>、22 份令牌解析、16 份 <c>OwnsAsync</c> 是同一段逻辑抄了多遍，
/// 改名 / 改判据要改几十处，漏一处就是一条没人复核的路由。
///
/// 两条规矩：
/// 1. **令牌只在管道里解析一次**：<see cref="UseTokenGate"/> 解析出来放进 HttpContext.Items，
///    解析不出来的就地回 401 <c>{"error":"invalid_token"}</c> 并短路，不进任何 route handler。
///    路由体里只调 <see cref="AccountOf"/> 取账号，不自己读令牌、不自己回 401。
/// 2. **归属只认一次只读探测**：<see cref="OwnsAsync"/> 按 account 读这个角色，
///    不是本人的角色一律当不存在（回 404 character_not_found），不许改成信请求体里的 account。
/// </summary>
public static class GatewayAuth
{
    /// <summary>
    /// 不要求令牌的路径：健康检查、登入（发令牌的地方本身）。
    /// 两条长连接在握手处各自验（<c>/battle</c>、<c>/world/stream</c>：认不过要按连接语义拒，
    /// 不是回一段 JSON），验法复用本类同一份 <see cref="TokenFrom"/>，所以也不走管道。
    /// </summary>
    static readonly string[] Exempt = { "/health", "/login", "/battle", "/world/stream" };

    /// <summary>管道解析出来的账号放在这里，键只有这一处。</summary>
    const string AccountKey = "gateway.account";

    public static bool IsExempt(string path)
    {
        for (var i = 0; i < Exempt.Length; i++)
        {
            if (string.Equals(path, Exempt[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 从请求里取令牌：查询串 <c>token</c> 优先（长连接与调试口这么带），
    /// 其次 <c>Authorization: Bearer</c>。两条格式都不认时回 null。
    /// </summary>
    public static string? TokenFrom(HttpRequest request)
    {
        var fromQuery = request.Query["token"].ToString();
        if (!string.IsNullOrEmpty(fromQuery))
        {
            return fromQuery;
        }

        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return header[prefix.Length..].Trim();
        }

        return null;
    }

    /// <summary>
    /// 解析令牌并把账号放进管道。解析不出来（空串 / 没发过 / 过期）回 false。
    /// </summary>
    public static bool TryResolve(HttpContext context, TokenStore tokens, out string account)
    {
        if (!tokens.TryResolve(
                GatewayAuth.TokenFrom(context.Request), DateTimeOffset.UtcNow, out account, out _))
        {
            account = "";
            return false;
        }

        context.Items[AccountKey] = account;
        return true;
    }

    /// <summary>
    /// 管道里那份账号。走管道就一定解析成功过；取不到只可能是路径在豁免表里
    /// （那几条路由本来就不认令牌），此时回空串，调用方按空账号处理。
    /// </summary>
    public static string AccountOf(HttpContext context) =>
        context.Items.TryGetValue(AccountKey, out var value) ? value as string ?? "" : "";

    /// <summary>
    /// 挂在管道上的那一道闸：除豁免表外，没令牌一律 401 且不读 body、不调任何 gRPC。
    /// 未注册的路径照旧交给后面的 404，不抢它的回包。
    /// </summary>
    public static void UseTokenGate(WebApplication app, TokenStore tokens)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (IsExempt(path) || context.GetEndpoint() is null)
            {
                await next();
                return;
            }

            if (!TryResolve(context, tokens, out _))
            {
                Console.WriteLine("auth_reject path=" + path + " reason=token");
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(
                    new { error = "invalid_token" }, context.RequestAborted);
                return;
            }

            await next();
        });
    }

    /// <summary>
    /// 归属复核：按令牌里的账号读一次这个角色，读得到才算本人的。
    /// 只读、不改库；角色不存在或不归这个账号都回 false，由调用方回 404 当它不存在。
    /// </summary>
    public static async Task<bool> OwnsAsync(
        CharacterService.CharacterServiceClient client,
        string account, ulong characterId, HttpContext context)
    {
        try
        {
            await client.ListInventoryAsync(
                new ListInventoryRequest { Account = account, CharacterId = characterId },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: context.RequestAborted);
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }
}

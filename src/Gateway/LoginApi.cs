using System.Text.Json;
using Npgsql;

namespace Gateway;

/// <summary>
/// 登录入口：口令换令牌。
///
/// 错误的账号和错误的口令回同一个 401，不区分「账号不存在」，
/// 也不在耗时上露馅：账号查不到时照样算一次同样参数的口令哈希。
/// 口令和令牌都不进日志，日志里只有账号名、结果和令牌长度。
/// </summary>
public static class LoginApi
{
    public sealed record LoginRequest(string? Account, string? Password);

    public sealed record LoginResponse(string Token, int ExpiresInS);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void Map(WebApplication app, AccountRepository accounts, TokenStore tokens, GatewayConfig config)
    {
        app.MapPost("/login", async context =>
        {
            LoginRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<LoginRequest>(
                    context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            var account = (request?.Account ?? "").Trim();
            var password = request?.Password ?? "";

            if (account.Length == 0 || password.Length == 0)
            {
                Console.WriteLine("login account= result=bad_request");
                await WriteAsync(context, 400, new { error = "bad_request" });
                return;
            }

            AccountRow? row;
            try
            {
                row = await accounts.FindAsync(account, context.RequestAborted);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                // 库连不上不是「口令错」，单独报，免得核对把它当成通过。
                Console.WriteLine("login account=" + account + " result=db_error " + ex.GetType().Name);
                await WriteAsync(context, 503, new { error = "account_store_unavailable" });
                return;
            }

            var iterations = row?.Iterations ?? config.Pbkdf2Iterations;

            // 哈希计算放到线程池，别占住 Kestrel 的请求线程。
            var matched = await Task.Run(
                () =>
                {
                    if (row is null || row.Disabled)
                    {
                        PasswordHasher.Burn(password, iterations);
                        return false;
                    }

                    return PasswordHasher.Verify(password, row.PasswordHash, row.PasswordSalt, row.Iterations);
                },
                context.RequestAborted);

            if (!matched)
            {
                Console.WriteLine("login account=" + account + " result=invalid");
                await WriteAsync(context, 401, new { error = "invalid_credentials" });
                return;
            }

            var (token, _) = tokens.Issue(config.TokenTtlSeconds);
            Console.WriteLine("login account=" + account
                + " result=ok token_len=" + token.Length
                + " ttl_s=" + config.TokenTtlSeconds);

            await WriteAsync(context, 200, new LoginResponse(token, config.TokenTtlSeconds));
        });
    }

    static async Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, body.GetType(), Json);
    }
}

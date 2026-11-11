using System;
using System.Globalization;
using Mmo.Net;

namespace Mmo.Tools;

/// <summary>
/// 入口两件事的纯 .NET 探针：直接编客户端那个 `AccountEntry.cs`，
/// 所以验到的就是随包发出去的那份拆装与文案口径。
///
/// 只吃命令行给的状态码与回包，不连网关——真正的 HTTP 由 check-u2.sh 用 curl 打，
/// 打回来的状态码与 body 原样喂进来，两端拼起来才算一条证据。
/// </summary>
public static class Program
{
    static int _fail;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("用法：");
            Console.WriteLine("  --login-body <账号> <口令>");
            Console.WriteLine("  --create-body <出身> <职业> <名号> <外观JSON>");
            Console.WriteLine("  --login <状态码> <回包>");
            Console.WriteLine("  --create <状态码> <回包>");
            return 2;
        }

        switch (args[0])
        {
            case "--login-body":
                Expect(args, 3);
                Console.WriteLine("body=" + AccountEntry.LoginBody(args[1], args[2]));
                return 0;
            case "--create-body":
                Expect(args, 5);
                Console.WriteLine("body=" + AccountEntry.CreateBody(args[3], args[1], args[4], args[2]));
                return 0;
            case "--login":
                Expect(args, 3);
                return PrintLogin(args[1], args[2]);
            case "--create":
                Expect(args, 3);
                return PrintCreate(args[1], args[2]);
            default:
                Console.WriteLine("未知参数：" + args[0]);
                return 2;
        }
    }

    static int PrintLogin(string statusText, string body)
    {
        var status = int.Parse(statusText, CultureInfo.InvariantCulture);
        var outcome = AccountEntry.ParseLogin(status, body);
        Console.WriteLine("kind=" + AccountEntry.KindName(outcome.Kind));
        Console.WriteLine("token_len=" + (outcome.Token ?? string.Empty).Length);
        Console.WriteLine("expires_in_s=" + outcome.ExpiresInS.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("error=" + (outcome.Error ?? string.Empty));
        Console.WriteLine("ok=" + (outcome.Ok ? "true" : "false"));
        return _fail;
    }

    static int PrintCreate(string statusText, string body)
    {
        var status = int.Parse(statusText, CultureInfo.InvariantCulture);
        var outcome = AccountEntry.ParseCreate(status, body);
        Console.WriteLine("kind=" + AccountEntry.KindName(outcome.Kind));
        Console.WriteLine("character_id=" + outcome.CharacterId.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("name=" + (outcome.Name ?? string.Empty));
        Console.WriteLine("error=" + (outcome.Error ?? string.Empty));
        Console.WriteLine("ok=" + (outcome.Ok ? "true" : "false"));
        return _fail;
    }

    static void Expect(string[] args, int count)
    {
        if (args.Length < count)
        {
            Console.WriteLine("参数不够，用不带参数跑一次看用法。");
            Environment.Exit(2);
        }
    }
}

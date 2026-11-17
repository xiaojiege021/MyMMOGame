using System.Runtime.InteropServices;

using static Character.Cli.CliFlags;

namespace Character.Cli;

/// <summary>
/// 进程入口：解析 → 只读核对 → 连库核对（含迁移）→ 启动；只做分发与启动，
/// 具体核对在 Cli/ 下的各域类里。
/// </summary>
internal static class CliEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parseCode = CliParser.Parse(args);
        if (parseCode != 0)
        {
            return parseCode;
        }

        // 只读核对不连库：坏配置在这一步就报 config_error。
        int? code;
        code = ConfigCli.TryReadOnly();
        if (code != null) { return code.Value; }

        code = ConfigTableCli.TryReadOnly();
        if (code != null) { return code.Value; }

        code = EnhanceCli.TryReadOnly();
        if (code != null) { return code.Value; }

        code = ShopCli.TryReadOnly();
        if (code != null) { return code.Value; }

        code = MainlineCli.TryReadOnly();
        if (code != null) { return code.Value; }

        code = MetaCli.TryReadOnly();
        if (code != null) { return code.Value; }

        // 连库核对与迁移；没有任何连库 flag 时直接落到启动。
        code = await DbCli.TryRunAsync();
        if (code != null) { return code.Value; }

        using var stop = new CancellationTokenSource();
        PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });
        PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });

        return await new CharacterProcess(configPath).RunAsync(stop.Token);
    }
}

using System;
using System.Collections.Generic;
using System.IO;

// 拆分卡之后（开发架构 14.8 纪律第 8 条）：一个类型可能由「主文件 + 同域分片目录里的
// partial 分片」组成。编辑器自检仍按老路径传主文件，这里把分片拼上去——断言文本一个字
// 不改，改的只是「怎么读」。
//
// 同一张表有三份，改一处要同步改三处：
//   - bash 段：tools/check/lib-common.sh 的 split_parts
//   - python 段：tools/check/lib_read.py 的 PARTS
//   - 这里：SplitRead.Parts
//
// 顺序：主文件在前，分片按文件名排序（与 src_agg 同口径）。凡是用注释锚点「切到末尾」
// 的断言都依赖这个顺序，别把分片排到前面。
// 服务端那批分片（CharacterApi / WorldApi / WorldGrpcService …）由 N4Check 与 P13bCheck
// 自己带着读法，不进这张表，免得同一个键两处口径。
public static class SplitRead
{
    static readonly Dictionary<string, string> Parts = new Dictionary<string, string>
    {
        // D13：客户端 Ui 三个大文件 + 战斗 HUD / 战斗会话 + 表世界拆装层
        { "UiShell.cs", "Shell" },
        { "UiSocialFlow.cs", "Flow" },
        { "UiWorldFlow.cs", "World" },
        { "BattleHud.cs", "Hud" },
        { "BattleClient.cs", "Session" },
        { "WorldEntry.cs", "WorldEntry" },
    };

    /// <summary>主文件 + 分片目录里的 *.cs。没登记或目录不存在时就是主文件本身。</summary>
    public static string Of(string path)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        if (!Parts.TryGetValue(Path.GetFileName(path), out var sub))
        {
            return text;
        }

        var dir = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, sub);
        if (!Directory.Exists(dir))
        {
            return text;
        }

        var files = new List<string>(Directory.GetFiles(dir, "*.cs"));
        files.Sort(StringComparer.Ordinal);
        foreach (var file in files)
        {
            text += File.ReadAllText(file);
        }

        return text;
    }
}

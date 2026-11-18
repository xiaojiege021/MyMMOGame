using System.Text.Json;

namespace Battle;

/// <summary>
/// Q9/H4：断线宽限、重连窗口与逃跑惩罚的唯一正本（config/reconnect.json）。
///
/// 三个数字各有各的意思，不要互相替代：
///   - battle_grace_ms：掉线后位置、血量、冷却与效果还留在这一场里的时长。
///     这段时间 Boss 与 DoT 照常按服务端时钟走（Q2 同一套时钟），玩家回来是续战。
///   - reconnect_window_s：从掉线那一刻算起，还能回这一场的上限。超过才按逃跑处理。
///   - deserter_penalty_s：逃跑者下一次进本要等的时长，由战斗服按本文件算出毫秒交给
///     角色服落事实值——角色服不读本文件，所以这里改数字不用同步第二份配置。
///
/// 缺表（老卡临时配置目录）时是 <see cref="Missing"/>：断线即离场、不重连、不惩罚，
/// 老链路一字不变；显式写了路径却缺文件照样起不来。
/// </summary>
public sealed class ReconnectTable
{
    public const string Schema = "reconnect/1";

    static readonly string[] TopKeys =
    [
        "schema", "version", "pending", "note",
        "battle_grace_ms", "reconnect_window_s", "deserter_penalty_s", "penalty_note",
    ];

    ReconnectTable(bool missing, int version, long graceMs, long windowMs, long penaltyMs)
    {
        IsMissing = missing;
        Version = version;
        GraceMs = graceMs;
        WindowMs = windowMs;
        PenaltyMs = penaltyMs;
    }

    /// <summary>没配这张表：不重连、不惩罚，断线照老口径直接离场。</summary>
    public bool IsMissing { get; }

    public int Version { get; }

    public long GraceMs { get; }

    public long WindowMs { get; }

    public long PenaltyMs { get; }

    public static ReconnectTable Missing => new(true, 0, 0, 0, 0);

    public static ReconnectTable Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"断线重连表不存在：{path}");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{path}：JSON 读不了，{ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException($"{path}：顶层必须是对象");
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!TopKeys.Contains(property.Name, StringComparer.Ordinal))
                {
                    throw new ConfigException($"{path}：不认识的键 {property.Name}");
                }
            }

            var schema = ReadString(root, "schema", path);
            if (schema != Schema)
            {
                throw new ConfigException($"{path}：schema 必须是 {Schema}，实际是 {schema}");
            }

            var version = ReadLong(root, "version", path);
            if (version < 1)
            {
                throw new ConfigException($"{path}：version 必须 >= 1");
            }

            var graceMs = ReadLong(root, "battle_grace_ms", path);
            var windowS = ReadLong(root, "reconnect_window_s", path);
            var penaltyS = ReadLong(root, "deserter_penalty_s", path);

            if (graceMs <= 0)
            {
                throw new ConfigException($"{path}：battle_grace_ms 必须 > 0");
            }

            if (windowS <= 0)
            {
                throw new ConfigException($"{path}：reconnect_window_s 必须 > 0");
            }

            if (penaltyS < 0)
            {
                throw new ConfigException($"{path}：deserter_penalty_s 不能为负");
            }

            // 宽限期比窗口还长就没意义了：位置还没失效，窗口已经算他逃跑。
            if (graceMs > windowS * 1000)
            {
                throw new ConfigException(
                    $"{path}：battle_grace_ms（{graceMs}）不能超过 reconnect_window_s（{windowS} 秒）");
            }

            return new ReconnectTable(false, (int)version, graceMs, windowS * 1000, penaltyS * 1000);
        }
    }

    static string ReadString(JsonElement root, string name, string path)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ConfigException($"{path}：缺 {name}（字符串）");
        }

        var text = value.GetString() ?? "";
        if (text.Trim().Length == 0)
        {
            throw new ConfigException($"{path}：{name} 不能为空");
        }

        return text.Trim();
    }

    static long ReadLong(JsonElement root, string name, string path)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var number))
        {
            throw new ConfigException($"{path}：缺 {name}（整数）");
        }

        return number;
    }
}

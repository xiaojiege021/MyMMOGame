using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mmo.Shared.Domain;

/// <summary>
/// 共享域里几张表的统一读法：snake_case 对齐 JSON，允许注释和尾逗号。
/// 读不了 / 内容为空都转成 ConfigException，启动壳只认这一种配置错误。
/// </summary>
public static class ConfigJson
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static T Read<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
                   ?? throw new ConfigException($"{path}：内容为空");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{path}：JSON 读不了，{ex.Message}");
        }
        catch (IOException ex)
        {
            throw new ConfigException($"{path}：文件读不了，{ex.Message}");
        }
    }
}

using System.Text.Json;

namespace Character;

/// <summary>
/// 建号只认配置里有的职业（config/classes.json，与战斗服同一份）。
/// 这里只读编号和名字，属性与技能仍由战斗服按同一张表算。
/// </summary>
public sealed class ClassIds
{
    readonly HashSet<string> _ids;

    public ClassIds(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到职业表：" + path);
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            _ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in doc.RootElement.GetProperty("classes").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _ids.Add(id.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ConfigException("职业表读不出来：" + ex.Message);
        }

        if (_ids.Count == 0)
        {
            throw new ConfigException("职业表里一个职业都没有：" + path);
        }
    }

    public bool Contains(string classId) => _ids.Contains(classId);
}

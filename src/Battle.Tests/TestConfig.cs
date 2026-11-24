using System.Text.Json;
using System.Text.Json.Nodes;

namespace Battle.Tests;

/// <summary>
/// 读正本配置、写临时副本的公共部分。副本都落在测试输出的 config 目录里，
/// 这样配置里按文件名相对引用的表（skills / effects / monsters …）仍然解析得到。
/// </summary>
static class TestConfig
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "config");

    public static string Real(string name) => Path.Combine(Dir, name);

    public static JsonNode ReadReal(string name) => JsonNode.Parse(File.ReadAllText(Real(name)))!;

    public static string WriteTemp(JsonNode root, string name)
    {
        var path = Path.Combine(Dir, "tmp-" + Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>改一处再落盘：测试只关心「这一处坏了就起不来」。</summary>
    public static string Mutate(string name, Action<JsonNode> change)
    {
        var root = ReadReal(name);
        change(root);
        return WriteTemp(root, name);
    }

    public static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 测试收尾删不掉不影响判定。
            }
        }
    }
}

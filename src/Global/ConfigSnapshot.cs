namespace Global;

public sealed class ConfigSnapshot
{
    public ConfigSnapshot(IReadOnlyDictionary<string, string> documents)
    {
        Documents = documents;
    }

    public IReadOnlyDictionary<string, string> Documents { get; }

    public string Read(string name)
    {
        return Documents[name];
    }
}

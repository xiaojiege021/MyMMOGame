namespace Global;

public sealed class ConfigCatalog : IDisposable
{
    private readonly string _root;
    private readonly object _gate = new();
    private ConfigSnapshot _current;
    private FileSystemWatcher? _watcher;

    public ConfigCatalog(string root)
    {
        _root = root;
        _current = Load();
    }

    public ConfigSnapshot Enter()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    public void Reload()
    {
        var next = Load();
        lock (_gate)
        {
            _current = next;
        }
    }

    public void Watch()
    {
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            Filter = "*.json",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        FileSystemEventHandler reload = (_, _) =>
        {
            try
            {
                Reload();
            }
            catch (IOException)
            {
            }
        };
        _watcher.Changed += reload;
        _watcher.Created += reload;
        _watcher.Renamed += (_, _) =>
        {
            try
            {
                Reload();
            }
            catch (IOException)
            {
            }
        };
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }

    private ConfigSnapshot Load()
    {
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(_root, file).Replace('\\', '/');
                documents[name] = File.ReadAllText(file);
            }
        }

        return new ConfigSnapshot(documents);
    }
}

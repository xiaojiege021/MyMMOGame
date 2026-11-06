namespace Global;

public sealed class ConfigSelfCheck
{
    public async Task<int> RunAsync()
    {
        var repoConfig = FindRepoConfig();
        using (var repo = new ConfigCatalog(repoConfig))
        {
            if (repo.Enter().Read("probe.json").Contains("\"first\"", StringComparison.Ordinal) == false)
            {
                Console.WriteLine("repo_probe=missing");
                return 1;
            }
        }

        var root = Path.Combine(Path.GetTempPath(), "mmo-e4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "table.json");
        await File.WriteAllTextAsync(file, "{\"label\":\"first\"}");
        try
        {
            using var catalog = new ConfigCatalog(root);
            var held = catalog.Enter();
            catalog.Watch();
            await File.WriteAllTextAsync(file, "{\"label\":\"second\"}");

            var sawNew = false;
            for (var i = 0; i < 30 && sawNew == false; i++)
            {
                await Task.Delay(100);
                sawNew = catalog.Enter().Read("table.json").Contains("\"second\"", StringComparison.Ordinal);
            }

            if (sawNew == false)
            {
                catalog.Reload();
            }

            var fresh = catalog.Enter();
            var heldText = held.Read("table.json");
            var freshText = fresh.Read("table.json");
            if (ReferenceEquals(held, fresh)
                || heldText.Contains("\"first\"", StringComparison.Ordinal) == false
                || freshText.Contains("\"second\"", StringComparison.Ordinal) == false)
            {
                Console.WriteLine("held=" + heldText);
                Console.WriteLine("fresh=" + freshText);
                return 1;
            }

            Console.WriteLine("held=first");
            Console.WriteLine("fresh=second");
            Console.WriteLine("watch=" + (sawNew ? "yes" : "reload"));
            return 0;
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string FindRepoConfig()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var probe = Path.Combine(dir.FullName, "config", "probe.json");
            if (File.Exists(probe))
            {
                return Path.Combine(dir.FullName, "config");
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("config/probe.json");
    }
}

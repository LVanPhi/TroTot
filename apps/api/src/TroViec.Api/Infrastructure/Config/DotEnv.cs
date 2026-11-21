namespace TroViec.Api.Infrastructure.Config;

/// <summary>
/// Dev convenience: loads KEY=VALUE lines from the repo-root .env (found by walking up to AGENTS.md).
/// Variables already set in the environment win, so containers and CI configure without a file.
/// Same rules as TroViec.Migrator's loader.
/// </summary>
public static class DotEnv
{
    public static void LoadFromRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) continue;
            var path = Path.Combine(dir.FullName, ".env");
            if (File.Exists(path)) Load(File.ReadAllLines(path));
            return;
        }
    }

    internal static void Load(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim();
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, trimmed[(eq + 1)..].Trim());
        }
    }
}

namespace TroViec.Migrator;

public static class Settings
{
    public static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new MigratorException($"environment variable {name} is not set");
}

public static class RepoRoot
{
    /// <summary>Walks up from the current directory to the folder containing AGENTS.md.</summary>
    public static string Find()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
                return dir.FullName;
        }
        throw new MigratorException("repository root (AGENTS.md) not found; run from inside the repo");
    }
}

/// <summary>
/// Minimal .env reader: KEY=VALUE per line, '#' comments. Variables already set in the environment win,
/// so CI and production can configure without a file.
/// </summary>
public static class DotEnv
{
    public static void LoadFromRepoRoot()
    {
        string path;
        try { path = Path.Combine(RepoRoot.Find(), ".env"); }
        catch (MigratorException) { return; }
        if (!File.Exists(path)) return;

        foreach (var line in File.ReadAllLines(path))
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

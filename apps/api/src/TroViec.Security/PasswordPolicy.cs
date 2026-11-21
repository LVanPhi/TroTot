using System.Reflection;

namespace TroViec.Security;

/// <summary>US-101 AC4: at least 10 characters and not among the 10,000 most common passwords.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;

    private static readonly Lazy<HashSet<string>> CommonPasswords = new(LoadCommonPasswords);

    /// <summary>Returns null when acceptable, otherwise a reason (never contains the password).</summary>
    public static string? Validate(string password)
    {
        if (password.Length < MinLength)
            return $"password must be at least {MinLength} characters";
        if (CommonPasswords.Value.Contains(password))
            return "password is too common";
        return null;
    }

    private static HashSet<string> LoadCommonPasswords()
    {
        // Source: SecLists Passwords/Common-Credentials/10k-most-common.txt (MIT).
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("common-passwords.txt")
            ?? throw new InvalidOperationException("embedded common-passwords.txt is missing");
        using var reader = new StreamReader(stream);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0) set.Add(line.Trim());
        }
        return set;
    }
}

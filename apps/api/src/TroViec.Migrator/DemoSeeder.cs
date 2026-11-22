using Npgsql;
using TroViec.Security;

namespace TroViec.Migrator;

/// <summary>
/// seed-demo: runs db/seed/demo/demo.sql in one transaction (idempotent). The demo password comes from
/// Demo__Password (default in db/seed/demo/README.md) and is stored as an Argon2id hash like any other password; the
/// student domain is the first of Auth__StudentEmailDomains so demo students pass BR-02. Never for real production data.
/// </summary>
public static class DemoSeeder
{
    public const string DefaultPassword = "Demo-TroViec-2026";

    public static async Task RunAsync(string connectionString, string seedFile, string password, string studentDomain,
        Action<string> log, CancellationToken ct)
    {
        if (PasswordPolicy.Validate(password) is { } reason) throw new MigratorException($"Demo__Password rejected: {reason}");
        if (!File.Exists(seedFile)) throw new MigratorException($"seed file not found: {seedFile}");

        // The hash and domain are inserted as SQL literals: neither can contain a quote (PHC base64 / domain charset).
        var hash = PasswordHasher.Hash(password);
        if (hash.Contains('\'') || studentDomain.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))
            throw new MigratorException("unexpected characters in hash or student domain");
        var sql = (await File.ReadAllTextAsync(seedFile, ct))
            .Replace("{{PASSWORD_HASH}}", hash, StringComparison.Ordinal)
            .Replace("{{STUDENT_DOMAIN}}", studentDomain, StringComparison.Ordinal);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var command = new NpgsqlCommand(sql, connection, tx)) await command.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        log("demo data ready (accounts, 20 rooms, 10 jobs); see db/seed/demo/README.md");
    }
}

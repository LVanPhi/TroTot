using Dapper;
using Npgsql;

namespace TroViec.Migrator;

public static class AdminBootstrapper
{
    /// <summary>
    /// Creates the first admin if no user has this email. Returns the user id and whether it was created.
    /// Logs only ids, never the email or password (AGENTS.md rule 13).
    /// </summary>
    public static async Task<(long UserId, bool Created)> RunAsync(
        string connectionString, string email, string password, Action<string> log, CancellationToken ct)
    {
        if (PasswordPolicy.Validate(password) is { } reason)
            throw new MigratorException($"Bootstrap__AdminPassword rejected: {reason}");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var existingId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT id FROM app.users WHERE lower(email) = lower(@Email)",
            new { Email = email }, tx, cancellationToken: ct));
        if (existingId is { } id)
        {
            log($"admin already exists (user id {id}); nothing to do");
            return (id, false);
        }

        var userId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO app.users (email, email_verified_at, password_hash, full_name)
            VALUES (@Email, now(), @PasswordHash, 'Quản trị viên')
            RETURNING id
            """,
            new { Email = email, PasswordHash = PasswordHasher.Hash(password) }, tx, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO app.user_roles (user_id, role_id, granter_id)
            SELECT @UserId, r.id, app.system_user_id() FROM app.roles r WHERE r.code = 'admin'
            """,
            new { UserId = userId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        log($"created admin user id {userId}; remove Bootstrap__* from .env now");
        return (userId, true);
    }
}

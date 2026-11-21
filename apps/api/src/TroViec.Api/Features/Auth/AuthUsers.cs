using Dapper;
using Npgsql;

namespace TroViec.Api.Features.Auth;

/// <summary>The user columns the auth flows need. Email lookups are case-insensitive (uq_users_email is on lower(email)).</summary>
public sealed record AuthUser(long Id, string? Email, string FullName, string PasswordHash, string Status, DateTime? EmailVerifiedAt);

public static class AuthUsers
{
    private const string Columns = "id, email, full_name, password_hash, status::text AS status, email_verified_at";

    public static Task<AuthUser?> ByEmailAsync(NpgsqlConnection connection, string email, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<AuthUser>(new CommandDefinition(
            $"SELECT {Columns} FROM app.users WHERE lower(email) = lower(@Email)", new { Email = email.Trim() },
            cancellationToken: ct));

    public static Task<AuthUser?> ByIdAsync(NpgsqlConnection connection, long id, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<AuthUser>(new CommandDefinition(
            $"SELECT {Columns} FROM app.users WHERE id = @Id", new { Id = id }, cancellationToken: ct));
}

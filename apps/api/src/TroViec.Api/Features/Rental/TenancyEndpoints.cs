using Dapper;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Problems;
using static TroViec.Api.Features.Rental.LeaseStore;

namespace TroViec.Api.Features.Rental;

/// <summary>/api/v1/tenancy: invites and leases seen by the tenant (US-1103; docs/12-rental.md).</summary>
public static class TenancyEndpoints
{
    public sealed record InviteSummary(Guid LeaseId, string LandlordName, string PropertyName, string StreetAddress, string RoomCode,
        DateOnly StartDate, DateOnly EndDate, int RentVnd, int DepositVnd, DateTime ExpiresAt);

    public sealed record ClaimedInvite(Guid LeaseId);

    public static void MapTenancy(this IEndpointRouteBuilder app)
    {
        var tenancy = app.MapGroup("/api/v1/tenancy").WithTags("Rental");
        // Viewing needs only a session: an owner who is invited can read the invite before getting rental.tenant.
        tenancy.MapGet("/invites/{token}", InviteAsync).RequireAuthorization().Produces<InviteSummary>();
        tenancy.MapPost("/invites/{token}/claim", ClaimAsync).RequireAuthorization(Permissions.RentalTenant).Produces<ClaimedInvite>();
        tenancy.MapGet("/leases", ListAsync).RequireAuthorization(Permissions.RentalTenant).Produces<IReadOnlyList<LeaseSummary>>();
        tenancy.MapGet("/leases/{leaseId:guid}", DetailAsync).RequireAuthorization(Permissions.RentalTenant).Produces<LeaseDetail>();
    }

    private sealed class InviteRow
    {
        public long PartyId { get; init; }
        public long LeaseId { get; init; }
        public long? UserId { get; init; }
        public string InviteEmail { get; init; } = "";
    }

    /// <summary>
    /// The invite of this token for the caller. Wrong email → 404 (the invite's owner is not revealed, US-1103 AC1);
    /// expired or cancelled → 410 rental.invite_expired (AC2).
    /// </summary>
    private static async Task<(InviteRow Invite, LeaseRow Lease)> InviteForCallerAsync(
        NpgsqlConnection c, NpgsqlTransaction? tx, string token, long userId, TimeProvider clock, CancellationToken ct)
    {
        if (token.Length is < 20 or > 100) throw new ProblemException("resource.not_found");
        var invite = await c.QuerySingleOrDefaultAsync<InviteRow>(new CommandDefinition(
            """
            SELECT id AS party_id, lease_id, user_id, invite_email
            FROM app.lease_parties WHERE invite_token_hash = @Hash AND role = 'tenant'
            """ + (tx is null ? "" : " FOR UPDATE"),
            new { Hash = LeaseEndpoints.TokenHash(token) }, tx, cancellationToken: ct)) ?? throw new ProblemException("resource.not_found");
        var email = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT lower(email) FROM app.users WHERE id = @Id AND email_verified_at IS NOT NULL", new { Id = userId }, tx, cancellationToken: ct));
        if (email != invite.InviteEmail.ToLowerInvariant() || (invite.UserId is { } claimed && claimed != userId))
            throw new ProblemException("resource.not_found");
        var lease = (await ByIdAsync(c, tx, invite.LeaseId, ct))!;
        if (lease.Status != "awaiting_signatures" || lease.InviteExpiresAt <= clock.GetUtcNow().UtcDateTime)
            throw new ProblemException("rental.invite_expired");
        return (invite, lease);
    }

    public static async Task<IResult> InviteAsync(string token, HttpContext http, NpgsqlDataSource db, TimeProvider clock, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var (_, lease) = await InviteForCallerAsync(connection, null, token, CurrentUser.Id(http.User), clock, ct);
        var landlord = (await PartiesAsync(connection, null, lease.Id, ct)).Single(p => p.Role == "landlord");
        return Results.Ok(new InviteSummary(lease.PublicId, landlord.FullName ?? "", lease.PropertyName, lease.StreetAddress,
            lease.RoomCode, DateOnly.FromDateTime(lease.StartDate), DateOnly.FromDateTime(lease.EndDate), lease.RentVnd,
            lease.DepositVnd, lease.InviteExpiresAt!.Value));
    }

    public static async Task<IResult> ClaimAsync(string token, HttpContext http, NpgsqlDataSource db, TimeProvider clock, CancellationToken ct)
    {
        var userId = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var (invite, lease) = await InviteForCallerAsync(connection, tx, token, userId, clock, ct);
        if (invite.UserId is null)
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE app.lease_parties SET user_id = @User, claimed_at = @Now WHERE id = @Id",
                new { User = userId, Now = clock.GetUtcNow().UtcDateTime, Id = invite.PartyId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return Results.Ok(new ClaimedInvite(lease.PublicId));
    }

    public static async Task<IResult> ListAsync(HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = await LeaseStore.ListAsync(connection,
            "EXISTS (SELECT 1 FROM app.lease_parties lp WHERE lp.lease_id = l.id AND lp.role = 'tenant' AND lp.user_id = @Me)",
            new { Me = CurrentUser.Id(http.User) }, ct);
        return Results.Ok(rows.Select(Summary).ToList());
    }

    public static async Task<IResult> DetailAsync(Guid leaseId, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var me = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        var lease = await ByPublicIdAsync(connection, null, leaseId, ct) ?? throw new ProblemException("resource.not_found");
        var parties = await PartiesAsync(connection, null, lease.Id, ct);
        if (!parties.Any(p => p.Role == "tenant" && p.UserId == me)) throw new ProblemException("resource.not_found");
        return Results.Ok(Detail(lease, parties, me));
    }
}

using System.Text.Json;
using Dapper;
using Npgsql;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Rental;

/// <summary>Reads of leases and their parties shared by the landlord, party and tenant endpoints.</summary>
public static class LeaseStore
{
    public sealed record ServiceItem(string Label, int AmountVnd);

    public sealed record PartyItem(string Role, string Name, string Email, bool Claimed, DateTime? ConfirmedAt, bool IsMe);

    public sealed record LeaseDetail(
        Guid LeaseId, string Status, long RoomId, string PropertyName, string StreetAddress, string WardCode, string RoomCode,
        DateOnly StartDate, DateOnly EndDate, int RentVnd, int DepositVnd, short BillingDay, int ElectricityPriceVnd,
        int WaterPriceVnd, string WaterUnit, IReadOnlyList<ServiceItem> Services, string? ExtraTerms, string? ContentSha256,
        DateTime? SentAt, DateTime? InviteExpiresAt, DateTime? ActivatedAt, DateTime? EndedAt, DateTime? CancelledAt,
        int Version, int TemplateVersion, IReadOnlyList<PartyItem> Parties);

    public sealed record LeaseSummary(Guid LeaseId, string Status, long RoomId, string PropertyName, string RoomCode,
        DateOnly StartDate, DateOnly EndDate, int RentVnd, DateTime? InviteExpiresAt, int Version);

    public sealed class LeaseRow
    {
        public long Id { get; init; }
        public Guid PublicId { get; init; }
        public long RoomId { get; init; }
        public string Status { get; init; } = "";
        public DateTime StartDate { get; init; }
        public DateTime EndDate { get; init; }
        public int RentVnd { get; init; }
        public int DepositVnd { get; init; }
        public short BillingDay { get; init; }
        public int ElectricityPriceVnd { get; init; }
        public int WaterPriceVnd { get; init; }
        public string WaterUnit { get; init; } = "";
        public string Services { get; init; } = "[]";
        public string? ExtraTerms { get; init; }
        public byte[]? ContentSha256 { get; init; }
        public DateTime? SentAt { get; init; }
        public DateTime? InviteExpiresAt { get; init; }
        public DateTime? ActivatedAt { get; init; }
        public DateTime? EndedAt { get; init; }
        public DateTime? CancelledAt { get; init; }
        public int Version { get; init; }
        public long OwnerId { get; init; }
        public string PropertyName { get; init; } = "";
        public string StreetAddress { get; init; } = "";
        public string WardCode { get; init; } = "";
        public string RoomCode { get; init; } = "";

        public IReadOnlyList<ServiceItem> ServiceItems =>
            JsonSerializer.Deserialize<List<ServiceItem>>(Services, JsonOptions) ?? [];
    }

    public sealed class PartyRow
    {
        public long Id { get; init; }
        public string Role { get; init; } = "";
        public long? UserId { get; init; }
        public string? InviteEmail { get; init; }
        public DateTime? ClaimedAt { get; init; }
        public DateTime? ConfirmedAt { get; init; }
        public string? DisplayName { get; init; }
        public string? FullName { get; init; }
        public string? UserEmail { get; init; }
        public string? UserStatus { get; init; }
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string LeaseSelect = """
        SELECT l.id, l.public_id, l.room_id, l.status::text AS status, l.start_date, l.end_date, l.rent_vnd, l.deposit_vnd,
               l.billing_day, l.electricity_price_vnd, l.water_price_vnd, l.water_unit, l.services::text AS services,
               l.extra_terms, l.content_sha256, l.sent_at, l.invite_expires_at, l.activated_at, l.ended_at, l.cancelled_at,
               l.version, p.owner_id, p.name AS property_name, p.street_address, p.ward_code, r.code AS room_code
        FROM app.leases l
        JOIN app.rooms r ON r.id = l.room_id
        JOIN app.properties p ON p.id = r.property_id
        """;

    public static Task<LeaseRow?> ByPublicIdAsync(
        NpgsqlConnection c, NpgsqlTransaction? tx, Guid leaseId, CancellationToken ct, bool forUpdate = false) =>
        c.QuerySingleOrDefaultAsync<LeaseRow>(new CommandDefinition(
            LeaseSelect + " WHERE l.public_id = @Id" + (forUpdate ? " FOR UPDATE OF l" : ""), new { Id = leaseId }, tx,
            cancellationToken: ct));

    public static Task<LeaseRow?> ByIdAsync(NpgsqlConnection c, NpgsqlTransaction? tx, long id, CancellationToken ct) =>
        c.QuerySingleOrDefaultAsync<LeaseRow>(new CommandDefinition(
            LeaseSelect + " WHERE l.id = @Id", new { Id = id }, tx, cancellationToken: ct));

    /// <summary>A lease of the caller's rooms; another landlord's lease is 404.</summary>
    public static async Task<LeaseRow> OwnedAsync(
        NpgsqlConnection c, NpgsqlTransaction? tx, Guid leaseId, long owner, CancellationToken ct, bool forUpdate = false)
    {
        var lease = await ByPublicIdAsync(c, tx, leaseId, ct, forUpdate);
        return lease is not null && lease.OwnerId == owner ? lease : throw new ProblemException("resource.not_found");
    }

    public static async Task<IReadOnlyList<PartyRow>> PartiesAsync(NpgsqlConnection c, NpgsqlTransaction? tx, long leaseId, CancellationToken ct) =>
        (await c.QueryAsync<PartyRow>(new CommandDefinition(
            """
            SELECT lp.id, lp.role::text AS role, lp.user_id, lp.invite_email, lp.claimed_at, lp.confirmed_at, lp.display_name,
                   u.full_name, u.email AS user_email, u.status::text AS user_status
            FROM app.lease_parties lp
            LEFT JOIN app.users u ON u.id = lp.user_id
            WHERE lp.lease_id = @Lease
            ORDER BY lp.role, lower(coalesce(lp.invite_email, '')), lp.id
            """, new { Lease = leaseId }, tx, cancellationToken: ct))).ToList();

    public static LeaseContent.Input ContentInput(LeaseRow l, IReadOnlyList<PartyRow> parties) => new(
        l.PublicId, l.PropertyName, l.StreetAddress, l.WardCode, l.RoomCode,
        parties.Single(p => p.Role == "landlord").UserEmail ?? "",
        parties.Where(p => p.Role == "tenant").Select(p => p.InviteEmail!).ToList(),
        DateOnly.FromDateTime(l.StartDate), DateOnly.FromDateTime(l.EndDate), l.RentVnd, l.DepositVnd, l.BillingDay,
        l.ElectricityPriceVnd, l.WaterPriceVnd, l.WaterUnit,
        l.ServiceItems.Select(s => new LeaseContent.Service(s.Label, s.AmountVnd)).ToList(), l.ExtraTerms);

    public static LeaseDetail Detail(LeaseRow l, IReadOnlyList<PartyRow> parties, long viewer) => new(
        l.PublicId, l.Status, l.RoomId, l.PropertyName, l.StreetAddress, l.WardCode, l.RoomCode,
        DateOnly.FromDateTime(l.StartDate), DateOnly.FromDateTime(l.EndDate), l.RentVnd, l.DepositVnd, l.BillingDay,
        l.ElectricityPriceVnd, l.WaterPriceVnd, l.WaterUnit, l.ServiceItems, l.ExtraTerms,
        l.ContentSha256 is null ? null : LeaseContent.Hex(l.ContentSha256),
        l.SentAt, l.InviteExpiresAt, l.ActivatedAt, l.EndedAt, l.CancelledAt, l.Version, LeaseContent.TemplateVersion,
        parties.Select(p => new PartyItem(
            p.Role,
            PartyName(p),
            (p.Role == "landlord" ? p.UserEmail : p.InviteEmail) ?? "",
            p.UserId is not null,
            p.ConfirmedAt,
            p.UserId == viewer)).ToList());

    /// <summary>BR-34: an anonymized tenant shows as "Người thuê đã xóa tài khoản"; the lease row stays for the other side.</summary>
    private static string PartyName(PartyRow p) => p switch
    {
        { UserStatus: "anonymized" } => p.Role == "tenant" ? "Người thuê đã xóa tài khoản" : "Chủ trọ đã xóa tài khoản",
        { DisplayName: { } name } => name,
        { FullName: { } full } => full,
        _ => "Chưa nhận lời mời",
    };

    public static LeaseSummary Summary(LeaseRow l) => new(l.PublicId, l.Status, l.RoomId, l.PropertyName, l.RoomCode,
        DateOnly.FromDateTime(l.StartDate), DateOnly.FromDateTime(l.EndDate), l.RentVnd, l.InviteExpiresAt, l.Version);

    public static async Task<IReadOnlyList<LeaseRow>> ListAsync(
        NpgsqlConnection c, string where, object args, CancellationToken ct) =>
        (await c.QueryAsync<LeaseRow>(new CommandDefinition(
            LeaseSelect + " WHERE " + where + " ORDER BY l.created_at DESC LIMIT 200", args, cancellationToken: ct))).ToList();

    /// <summary>
    /// Room status follows its leases (US-1002 AC1): occupied with an active lease, reserved with one awaiting
    /// signatures, otherwise vacant. A room under maintenance without leases stays under maintenance.
    /// </summary>
    public static Task RecomputeRoomStatusAsync(NpgsqlConnection c, NpgsqlTransaction tx, long roomId, CancellationToken ct) =>
        c.ExecuteAsync(new CommandDefinition(
            """
            WITH s AS (
                SELECT CASE
                    WHEN EXISTS (SELECT 1 FROM app.leases WHERE room_id = @Room AND status = 'active') THEN 'occupied'
                    WHEN EXISTS (SELECT 1 FROM app.leases WHERE room_id = @Room AND status = 'awaiting_signatures') THEN 'reserved'
                    ELSE NULL END AS by_lease)
            UPDATE app.rooms r
            SET status = coalesce(s.by_lease::app.room_status,
                                  CASE WHEN r.status IN ('reserved', 'occupied') THEN 'vacant'::app.room_status ELSE r.status END),
                version = r.version + 1
            FROM s
            WHERE r.id = @Room
              AND r.status IS DISTINCT FROM coalesce(s.by_lease::app.room_status,
                                  CASE WHEN r.status IN ('reserved', 'occupied') THEN 'vacant'::app.room_status ELSE r.status END)
            """, new { Room = roomId }, tx, cancellationToken: ct));
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Features.Listings;
using TroViec.Api.Features.Me;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Notifications;
using TroViec.Api.Infrastructure.Problems;
using static TroViec.Api.Features.Rental.LeaseStore;

namespace TroViec.Api.Features.Rental;

/// <summary>
/// Leases (E11, BR-21…BR-27, BR-32, BR-33; docs/12-rental.md). The landlord drafts and sends; each invited tenant
/// claims the invite with the invited email; every party confirms with an email OTP over the frozen content hash;
/// when all have confirmed the lease is active, the room occupied and the room's verified listing back to draft.
/// </summary>
public static class LeaseEndpoints
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);
    public const string RoomLeasedReason = "Phòng đã cho thuê";

    public sealed record LeaseRequest(
        long RoomId, IReadOnlyList<string> TenantEmails, DateOnly StartDate, DateOnly EndDate, int RentVnd, int DepositVnd,
        short BillingDay, int ElectricityPriceVnd, int WaterPriceVnd, string WaterUnit, IReadOnlyList<ServiceItem>? Services,
        string? ExtraTerms);

    public sealed record LeaseUpdate(
        long RoomId, IReadOnlyList<string> TenantEmails, DateOnly StartDate, DateOnly EndDate, int RentVnd, int DepositVnd,
        short BillingDay, int ElectricityPriceVnd, int WaterPriceVnd, string WaterUnit, IReadOnlyList<ServiceItem>? Services,
        string? ExtraTerms, int Version)
    {
        public LeaseRequest Fields => new(RoomId, TenantEmails, StartDate, EndDate, RentVnd, DepositVnd, BillingDay,
            ElectricityPriceVnd, WaterPriceVnd, WaterUnit, Services, ExtraTerms);
    }

    public sealed record VersionRequest(int Version);

    public sealed record ConfirmRequest(string Code, string ContentSha256);

    public sealed record CreatedLease(Guid LeaseId, int Version);

    public sealed record SentLease(string Status, string ContentSha256, int Version);

    public sealed record StatusResponse(string Status);

    public sealed class LeaseValidator : AbstractValidator<LeaseRequest>
    {
        public LeaseValidator() => AddLeaseRules(this, r => r);
    }

    public sealed class LeaseUpdateValidator : AbstractValidator<LeaseUpdate>
    {
        public LeaseUpdateValidator()
        {
            AddLeaseRules(this, r => r.Fields);
            RuleFor(r => r.Version).GreaterThan(0).WithMessage("Thiếu phiên bản");
        }
    }

    public sealed class VersionValidator : AbstractValidator<VersionRequest>
    {
        public VersionValidator() => RuleFor(r => r.Version).GreaterThan(0).WithMessage("Thiếu phiên bản");
    }

    public sealed class ConfirmValidator : AbstractValidator<ConfirmRequest>
    {
        public ConfirmValidator()
        {
            RuleFor(r => r.Code).Matches("^[0-9]{6}$").WithMessage("Mã gồm 6 chữ số");
            RuleFor(r => r.ContentSha256).Matches("^[0-9a-fA-F]{64}$").WithMessage("Thiếu mã băm hợp đồng");
        }
    }

    private static void AddLeaseRules<T>(AbstractValidator<T> v, Func<T, LeaseRequest> f)
    {
        v.RuleFor(x => f(x).RoomId).GreaterThan(0).OverridePropertyName("roomId").WithMessage("Chọn phòng");
        v.RuleFor(x => f(x).TenantEmails)
            .Must(e => e is { Count: >= 1 and <= 4 }
                       && e.All(m => m is not null && m.Length <= 254 && System.Text.RegularExpressions.Regex.IsMatch(m.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                       && e.Select(m => m.Trim().ToLowerInvariant()).Distinct().Count() == e.Count)
            .OverridePropertyName("tenantEmails").WithMessage("1–4 email người thuê, không trùng nhau");
        v.RuleFor(x => f(x)).Must(r => r.StartDate < r.EndDate && r.EndDate >= r.StartDate.AddMonths(1)
                                       && r.EndDate <= r.StartDate.AddMonths(36))
            .OverridePropertyName("endDate").WithMessage("Thời hạn từ 1 đến 36 tháng");
        v.RuleFor(x => f(x).RentVnd).InclusiveBetween(100_000, 100_000_000).OverridePropertyName("rentVnd")
            .WithMessage("Tiền thuê từ 100.000 đến 100.000.000 ₫");
        v.RuleFor(x => f(x).DepositVnd).InclusiveBetween(0, 1_000_000_000).OverridePropertyName("depositVnd")
            .WithMessage("Tiền cọc không hợp lệ");
        v.RuleFor(x => f(x).BillingDay).InclusiveBetween((short)1, (short)28).OverridePropertyName("billingDay")
            .WithMessage("Ngày chốt hóa đơn từ 1 đến 28");
        v.RuleFor(x => f(x).ElectricityPriceVnd).InclusiveBetween(0, 100_000).OverridePropertyName("electricityPriceVnd")
            .WithMessage("Đơn giá điện không hợp lệ");
        v.RuleFor(x => f(x).WaterPriceVnd).InclusiveBetween(0, 1_000_000).OverridePropertyName("waterPriceVnd")
            .WithMessage("Đơn giá nước không hợp lệ");
        v.RuleFor(x => f(x).WaterUnit).Must(u => u is "m3" or "person").OverridePropertyName("waterUnit")
            .WithMessage("Tính nước theo m³ hoặc theo người");
        v.RuleFor(x => f(x).Services)
            .Must(s => s is null || (s.Count <= 10 && s.All(i => i is not null && i.Label?.Trim().Length is >= 1 and <= 100
                                                               && i.AmountVnd is >= 0 and <= 100_000_000)))
            .OverridePropertyName("services").WithMessage("Tối đa 10 khoản dịch vụ, tên 1–100 ký tự");
        v.RuleFor(x => f(x).ExtraTerms).Must(t => t is null || t.Trim().Length <= 3000).OverridePropertyName("extraTerms")
            .WithMessage("Điều khoản thêm tối đa 3.000 ký tự");
    }

    public static void MapLeases(this RouteGroupBuilder rental, IEndpointRouteBuilder app)
    {
        rental.MapGet("/leases", ListAsync).Produces<IReadOnlyList<LeaseSummary>>();
        rental.MapPost("/leases", CreateAsync).Validate<LeaseRequest>().Produces<CreatedLease>(StatusCodes.Status201Created);
        rental.MapGet("/leases/{leaseId:guid}", DetailAsync).Produces<LeaseDetail>();
        rental.MapPut("/leases/{leaseId:guid}", UpdateAsync).Validate<LeaseUpdate>().Produces<CreatedLease>();
        rental.MapPost("/leases/{leaseId:guid}/send", SendAsync).Validate<VersionRequest>()
            .Produces<SentLease>(StatusCodes.Status202Accepted);
        rental.MapPost("/leases/{leaseId:guid}/cancel", CancelAsync).Validate<VersionRequest>();
        rental.MapPost("/leases/{leaseId:guid}/end", EndAsync).Validate<VersionRequest>();

        // Any party of the lease: the landlord (rental.manage) or a tenant who claimed the invite (rental.tenant).
        // The party check below is the authorization; another person's lease answers 404.
        var parties = app.MapGroup("/api/v1/leases").WithTags("Rental").RequireAuthorization();
        parties.MapPost("/{leaseId:guid}/otp", SendCodeAsync);
        parties.MapPost("/{leaseId:guid}/confirm", ConfirmAsync).Validate<ConfirmRequest>().Produces<StatusResponse>();
    }

    // ---- landlord -------------------------------------------------------------------------------------------

    public static async Task<IResult> ListAsync(HttpContext http, NpgsqlDataSource db, CancellationToken ct,
        string? status = null, long? roomId = null)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = await LeaseStore.ListAsync(connection,
            "p.owner_id = @Owner AND (@Status::text IS NULL OR l.status::text = @Status) AND (@Room::bigint IS NULL OR l.room_id = @Room)",
            new { Owner = CurrentUser.Id(http.User), Status = status, Room = roomId }, ct);
        return Results.Ok(rows.Select(Summary).ToList());
    }

    public static async Task<IResult> CreateAsync(LeaseRequest request, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var owner = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await RequireOwnedRoomAsync(connection, tx, request.RoomId, owner, ct);
        await RequireTenantsAreNotLandlordAsync(connection, tx, owner, request.TenantEmails, ct);
        var (id, publicId) = await connection.QuerySingleAsync<(long, Guid)>(new CommandDefinition(
            """
            INSERT INTO app.leases (room_id, start_date, end_date, rent_vnd, deposit_vnd, billing_day, electricity_price_vnd,
                                    water_price_vnd, water_unit, services, extra_terms)
            VALUES (@RoomId, @StartDate::date, @EndDate::date, @RentVnd, @DepositVnd, @BillingDay, @ElectricityPriceVnd,
                    @WaterPriceVnd, @WaterUnit, @Services::jsonb, @ExtraTerms)
            RETURNING id, public_id
            """, LeaseArgs(request), tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO app.lease_parties (lease_id, role, user_id) VALUES (@Lease, 'landlord', @Owner)",
            new { Lease = id, Owner = owner }, tx, cancellationToken: ct));
        await InsertTenantsAsync(connection, tx, id, request.TenantEmails, ct);
        await tx.CommitAsync(ct);
        return Results.Created($"/api/v1/rental/leases/{publicId}", new CreatedLease(publicId, 1));
    }

    public static async Task<IResult> DetailAsync(Guid leaseId, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var owner = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        var lease = await OwnedAsync(connection, null, leaseId, owner, ct);
        return Results.Ok(Detail(lease, await PartiesAsync(connection, null, lease.Id, ct), owner));
    }

    public static async Task<IResult> UpdateAsync(Guid leaseId, LeaseUpdate update, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var owner = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var lease = await OwnedAsync(connection, tx, leaseId, owner, ct, forUpdate: true);
        if (lease.Status != "draft") throw new ProblemException("rental.lease_not_editable");
        if (lease.Version != update.Version) throw new ProblemException("rental.version_conflict");
        var request = update.Fields;
        await RequireOwnedRoomAsync(connection, tx, request.RoomId, owner, ct);
        await RequireTenantsAreNotLandlordAsync(connection, tx, owner, request.TenantEmails, ct);

        var args = new DynamicParameters(LeaseArgs(request));
        args.Add("Id", lease.Id);
        var version = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            UPDATE app.leases
            SET room_id = @RoomId, start_date = @StartDate::date, end_date = @EndDate::date, rent_vnd = @RentVnd,
                deposit_vnd = @DepositVnd, billing_day = @BillingDay, electricity_price_vnd = @ElectricityPriceVnd,
                water_price_vnd = @WaterPriceVnd, water_unit = @WaterUnit, services = @Services::jsonb,
                extra_terms = @ExtraTerms, version = version + 1
            WHERE id = @Id
            RETURNING version
            """, args, tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM app.lease_parties WHERE lease_id = @Id AND role = 'tenant'", new { lease.Id }, tx, cancellationToken: ct));
        await InsertTenantsAsync(connection, tx, lease.Id, request.TenantEmails, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new CreatedLease(leaseId, version));
    }

    /// <summary>
    /// US-1102: BR-21 (posting conditions + an active payout account), freeze content_sha256 (BR-24), one invite token
    /// per tenant (only its SHA-256 is stored), room → reserved, invites emailed. The response never says which
    /// invited emails already have an account (BR-27).
    /// </summary>
    public static async Task<IResult> SendAsync(
        Guid leaseId, VersionRequest request, HttpContext http, NpgsqlDataSource db, StudentEmailDomains studentDomains,
        Throttles throttles, TimeProvider clock, IEmailSender email, SiteUrl site, ILoggerFactory loggers, CancellationToken ct)
    {
        var owner = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await RequireLandlordReadyAsync(connection, owner, studentDomains, ct);
        if (!await throttles.TryHitAsync("lease-send", owner.ToString(System.Globalization.CultureInfo.InvariantCulture), 20, TimeSpan.FromDays(1)))
            throw new ProblemException("rate_limited");

        await using var tx = await connection.BeginTransactionAsync(ct);
        var lease = await OwnedAsync(connection, tx, leaseId, owner, ct, forUpdate: true);
        if (lease.Status != "draft") throw new ProblemException("rental.lease_not_editable");
        if (lease.Version != request.Version) throw new ProblemException("rental.version_conflict");
        var parties = await PartiesAsync(connection, tx, lease.Id, ct);
        var hash = LeaseContent.Hash(ContentInput(lease, parties));
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now + InviteLifetime;

        var invites = new List<(string Email, string Token)>();
        foreach (var tenant in parties.Where(p => p.Role == "tenant"))
        {
            var token = Secrets.NewToken();
            invites.Add((tenant.InviteEmail!, token));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE app.lease_parties SET invite_token_hash = @Hash WHERE id = @Id",
                new { Hash = TokenHash(token), tenant.Id }, tx, cancellationToken: ct));
        }
        var version = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            UPDATE app.leases
            SET status = 'awaiting_signatures', content_sha256 = @Hash, sent_at = @Now, invite_expires_at = @Expires,
                version = version + 1
            WHERE id = @Id
            RETURNING version
            """, new { Hash = hash, Now = now, Expires = expires, lease.Id }, tx, cancellationToken: ct));
        await RecomputeRoomStatusAsync(connection, tx, lease.RoomId, ct);
        await tx.CommitAsync(ct);

        var landlord = parties.Single(p => p.Role == "landlord").FullName ?? "Chủ trọ";
        var place = $"phòng {lease.RoomCode}, {lease.PropertyName}";
        var logger = loggers.CreateLogger("Leases");
        foreach (var (to, token) in invites)
        {
            try
            {
                await email.SendAsync(RentalEmailTemplates.LeaseInvite(to, landlord, place, site.Link($"/loi-moi/{token}"), expires), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Lease invite not sent for lease {LeaseId}: {ExceptionType}", leaseId, ex.GetType().Name);
            }
        }
        return Results.Accepted($"/api/v1/rental/leases/{leaseId}", new SentLease("awaiting_signatures", LeaseContent.Hex(hash), version));
    }

    public static async Task<IResult> CancelAsync(Guid leaseId, VersionRequest request, HttpContext http, NpgsqlDataSource db,
        TimeProvider clock, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var lease = await OwnedAsync(connection, tx, leaseId, CurrentUser.Id(http.User), ct, forUpdate: true);
        if (lease.Status is not ("draft" or "awaiting_signatures")) throw new ProblemException("rental.lease_not_editable");
        if (lease.Version != request.Version) throw new ProblemException("rental.version_conflict");
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.leases SET status = 'cancelled', cancelled_at = @Now, version = version + 1 WHERE id = @Id",
            new { Now = clock.GetUtcNow().UtcDateTime, lease.Id }, tx, cancellationToken: ct));
        await RecomputeRoomStatusAsync(connection, tx, lease.RoomId, ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>BR-33: the landlord ends an active lease now; the nightly job ends leases past end_date.</summary>
    public static async Task<IResult> EndAsync(Guid leaseId, VersionRequest request, HttpContext http, NpgsqlDataSource db,
        TimeProvider clock, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var lease = await OwnedAsync(connection, tx, leaseId, CurrentUser.Id(http.User), ct, forUpdate: true);
        if (lease.Status != "active") throw new ProblemException("rental.lease_not_editable");
        if (lease.Version != request.Version) throw new ProblemException("rental.version_conflict");
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.leases SET status = 'ended', ended_at = @Now, version = version + 1 WHERE id = @Id",
            new { Now = clock.GetUtcNow().UtcDateTime, lease.Id }, tx, cancellationToken: ct));
        await RecomputeRoomStatusAsync(connection, tx, lease.RoomId, ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    // ---- every party ----------------------------------------------------------------------------------------

    /// <summary>The caller's party row on a lease awaiting signatures; anything else about someone else's lease is 404.</summary>
    private static async Task<(LeaseRow Lease, IReadOnlyList<PartyRow> Parties, PartyRow Me)> PartyAsync(
        NpgsqlConnection c, NpgsqlTransaction? tx, Guid leaseId, long userId, TimeProvider clock, CancellationToken ct,
        bool forUpdate = false)
    {
        var lease = await ByPublicIdAsync(c, tx, leaseId, ct, forUpdate) ?? throw new ProblemException("resource.not_found");
        var parties = await PartiesAsync(c, tx, lease.Id, ct);
        var me = parties.FirstOrDefault(p => p.UserId == userId) ?? throw new ProblemException("resource.not_found");
        if (me.ConfirmedAt is not null || lease.Status is "active" or "ended") throw new ProblemException("rental.lease_already_confirmed");
        if (lease.Status != "awaiting_signatures" || lease.InviteExpiresAt <= clock.GetUtcNow().UtcDateTime)
            throw new ProblemException("rental.invite_expired");
        return (lease, parties, me);
    }

    public static async Task<IResult> SendCodeAsync(Guid leaseId, HttpContext http, NpgsqlDataSource db, LeaseCodes codes,
        Throttles throttles, TimeProvider clock, IEmailSender email, CancellationToken ct)
    {
        var userId = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        var (lease, _, me) = await PartyAsync(connection, null, leaseId, userId, clock, ct);
        var subject = userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!await throttles.TryHitAsync("lease-otp", $"{subject}:{lease.Id}", 3, TimeSpan.FromHours(1))
            || !await throttles.TryHitAsync("lease-otp-day", subject, 10, TimeSpan.FromDays(1)))
            throw new ProblemException("rate_limited");
        if (me.UserEmail is not { } to) throw new ProblemException("resource.not_found");
        var code = await codes.NewAsync(lease.Id, userId);
        await email.SendAsync(RentalEmailTemplates.LeaseCode(to, me.FullName ?? "", code), ct);
        return Results.NoContent();
    }

    /// <summary>
    /// BR-25: the code proves control of the party's verified email; the hash proves which content was read. The last
    /// confirmation activates the lease (room occupied, room listing verified → draft by the system, BR-32).
    /// </summary>
    public static async Task<IResult> ConfirmAsync(
        Guid leaseId, ConfirmRequest request, HttpContext http, NpgsqlDataSource db, LeaseCodes codes, TimeProvider clock,
        ListingTransitionService transitions, IEmailSender email, SiteUrl site, ILoggerFactory loggers, CancellationToken ct)
    {
        var userId = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        var (preview, _, _) = await PartyAsync(connection, null, leaseId, userId, clock, ct);
        if (!string.Equals(LeaseContent.Hex(preview.ContentSha256!), request.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new ProblemException("rental.lease_content_changed");
        switch (await codes.CheckAsync(preview.Id, userId, request.Code))
        {
            case Infrastructure.Auth.CodeCheck.Invalid: throw new ProblemException("auth.code_invalid");
            case Infrastructure.Auth.CodeCheck.AttemptsExceeded: throw new ProblemException("auth.code_attempts_exceeded");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await connection.BeginTransactionAsync(ct);
        var (lease, parties, me) = await PartyAsync(connection, tx, leaseId, userId, clock, ct, forUpdate: true);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.lease_parties SET confirmed_at = @Now WHERE id = @Id", new { Now = now, me.Id }, tx, cancellationToken: ct));
        var allConfirmed = parties.All(p => p.Id == me.Id || (p.UserId is not null && p.ConfirmedAt is not null));
        if (allConfirmed)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE app.leases SET status = 'active', activated_at = @Now, version = version + 1 WHERE id = @Id",
                new { Now = now, lease.Id }, tx, cancellationToken: ct));
            await RecomputeRoomStatusAsync(connection, tx, lease.RoomId, ct);
            await UnpublishRoomListingAsync(connection, tx, lease.RoomId, transitions, ct);
        }
        await tx.CommitAsync(ct);

        if (allConfirmed)
            await NotifyActivatedAsync(lease, parties, email, site, loggers.CreateLogger("Leases"), ct);
        return Results.Ok(new StatusResponse(allConfirmed ? "active" : "awaiting_signatures"));
    }

    private static async Task UnpublishRoomListingAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, long roomId, ListingTransitionService transitions, CancellationToken ct)
    {
        var listings = await c.QueryAsync<(long Id, int Version)>(new CommandDefinition(
            """
            SELECT l.id, l.version FROM app.listings l JOIN app.room_listings rl ON rl.listing_id = l.id
            WHERE rl.room_id = @Room AND l.status = 'verified'
            FOR UPDATE OF l
            """, new { Room = roomId }, tx, cancellationToken: ct));
        var system = await c.ExecuteScalarAsync<long>(new CommandDefinition("SELECT app.system_user_id()", transaction: tx, cancellationToken: ct));
        foreach (var (id, version) in listings)
            await transitions.TransitionAsync(c, tx, id, version, ListingStatus.Verified, ListingStatus.Draft, system,
                RoomLeasedReason, ct: ct);
    }

    private static async Task NotifyActivatedAsync(LeaseRow lease, IReadOnlyList<PartyRow> parties, IEmailSender email, SiteUrl site,
        ILogger logger, CancellationToken ct)
    {
        var place = $"phòng {lease.RoomCode}, {lease.PropertyName}";
        foreach (var p in parties)
        {
            if (p.UserEmail is not { } to) continue;
            var path = p.Role == "landlord" ? $"/chu-tro/hop-dong/{lease.PublicId}" : $"/toi/thue/hop-dong/{lease.PublicId}";
            try
            {
                await email.SendAsync(RentalEmailTemplates.LeaseActivated(to, p.FullName ?? "", place,
                    LeaseContent.Hex(lease.ContentSha256!), site.Link(path)), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Lease activation email not sent for lease {LeaseId}: {ExceptionType}", lease.PublicId, ex.GetType().Name);
            }
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    public static byte[] TokenHash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static object LeaseArgs(LeaseRequest r) => new
    {
        r.RoomId,
        StartDate = r.StartDate.ToDateTime(TimeOnly.MinValue),
        EndDate = r.EndDate.ToDateTime(TimeOnly.MinValue),
        r.RentVnd,
        r.DepositVnd,
        r.BillingDay,
        r.ElectricityPriceVnd,
        r.WaterPriceVnd,
        r.WaterUnit,
        Services = JsonSerializer.Serialize(
            (r.Services ?? []).Select(s => new ServiceItem(s.Label.Trim(), s.AmountVnd)).ToList(), JsonOptions),
        ExtraTerms = string.IsNullOrWhiteSpace(r.ExtraTerms) ? null : r.ExtraTerms.Trim(),
    };

    private static async Task InsertTenantsAsync(NpgsqlConnection c, NpgsqlTransaction tx, long leaseId,
        IReadOnlyList<string> emails, CancellationToken ct)
    {
        foreach (var address in emails)
            await c.ExecuteAsync(new CommandDefinition(
                "INSERT INTO app.lease_parties (lease_id, role, invite_email) VALUES (@Lease, 'tenant', @Email)",
                new { Lease = leaseId, Email = address.Trim().ToLowerInvariant() }, tx, cancellationToken: ct));
    }

    private static async Task RequireOwnedRoomAsync(NpgsqlConnection c, NpgsqlTransaction tx, long roomId, long owner, CancellationToken ct)
    {
        var owned = await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM app.rooms r JOIN app.properties p ON p.id = r.property_id WHERE r.id = @Room AND p.owner_id = @Owner)",
            new { Room = roomId, Owner = owner }, tx, cancellationToken: ct));
        if (!owned) throw new ProblemException("validation.failed", errors: new Dictionary<string, string[]> { ["roomId"] = ["Chọn phòng của bạn"] });
    }

    /// <summary>BR-23: the landlord cannot invite their own email as a tenant.</summary>
    private static async Task RequireTenantsAreNotLandlordAsync(NpgsqlConnection c, NpgsqlTransaction tx, long owner,
        IReadOnlyList<string> emails, CancellationToken ct)
    {
        var own = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT lower(email) FROM app.users WHERE id = @Id", new { Id = owner }, tx, cancellationToken: ct));
        if (own is not null && emails.Any(e => e.Trim().ToLowerInvariant() == own))
            throw new ProblemException("rental.invalid_party");
    }

    /// <summary>BR-21: posting conditions (BR-02) plus an active payout account; the missing steps are listed.</summary>
    private static async Task RequireLandlordReadyAsync(NpgsqlConnection c, long owner, StudentEmailDomains studentDomains, CancellationToken ct)
    {
        var me = await MeQueries.GetAsync(c, owner, studentDomains, ct) ?? throw new ProblemException("auth.unauthenticated");
        var missing = me.Posting.Missing.ToList();
        var payout = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT status::text FROM app.payout_accounts WHERE owner_id = @Id", new { Id = owner }, cancellationToken: ct));
        if (payout is null) missing.Add("payout_account");
        else if (payout == "flagged") missing.Add("payout_account_review");
        if (missing.Count > 0)
            throw new ProblemException("rental.landlord_not_ready", extensions: new Dictionary<string, object?> { ["missing"] = missing });
    }
}

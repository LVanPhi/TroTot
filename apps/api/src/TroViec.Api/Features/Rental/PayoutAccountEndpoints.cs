using System.Globalization;
using System.Text;
using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Crypto;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Rental;

/// <summary>
/// The landlord's payout account (US-1003, BR-31) and the moderator review of flagged accounts (US-1004).
/// The account number is stored AES-256-GCM encrypted (AAD "payout-account:{ownerId}") plus an HMAC that matches
/// app.identifiers; no endpoint returns the full number (docs/12-rental.md).
/// </summary>
public static class PayoutAccountEndpoints
{
    public sealed record PayoutRequest(short BankId, string AccountNumber, string HolderName);

    public sealed record PayoutResponse(short BankId, string BankShortName, string AccountHint, string HolderName, string Status);

    public sealed record ReviewRequest(string Decision, string Note);

    public sealed record FlaggedAccount(long OwnerId, string OwnerName, string BankShortName, string AccountHint,
        string HolderName, string Status, int ConfirmedReportCount, DateTime UpdatedAt, DateTime? ReviewedAt, string? ReviewNote);

    public sealed class PayoutValidator : AbstractValidator<PayoutRequest>
    {
        public PayoutValidator()
        {
            RuleFor(r => r.BankId).GreaterThan((short)0).WithMessage("Chọn ngân hàng");
            RuleFor(r => r.AccountNumber).Must(n => NormalizeAccount(n) is { Length: >= 6 and <= 19 })
                .WithMessage("Số tài khoản gồm 6–19 chữ số");
            RuleFor(r => r.HolderName).Must(n => NormalizeHolder(n) is { Length: >= 1 and <= 50 } h && h.All(c => c is ' ' or >= 'A' and <= 'Z'))
                .WithMessage("Tên chủ tài khoản như trên app ngân hàng, tối đa 50 ký tự");
        }
    }

    public sealed class ReviewValidator : AbstractValidator<ReviewRequest>
    {
        public ReviewValidator()
        {
            RuleFor(r => r.Decision).Must(d => d is "clear" or "keep").WithMessage("Chọn gỡ cờ hoặc giữ cờ");
            RuleFor(r => r.Note).Must(n => n is not null && n.Trim().Length is >= 1 and <= 500)
                .WithMessage("Ghi lý do (tối đa 500 ký tự)");
        }
    }

    public static void MapPayoutAccounts(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").WithTags("Rental").RequireAuthorization(Permissions.RentalManage);
        me.MapGet("/payout-account", GetAsync).Produces<PayoutResponse>();
        me.MapPut("/payout-account", PutAsync).Validate<PayoutRequest>().Produces<PayoutResponse>();

        var mod = app.MapGroup("/api/v1/moderation").WithTags("Moderation").RequireAuthorization(Permissions.OwnerVerify);
        mod.MapGet("/payout-accounts", ListFlaggedAsync).Produces<Paged<FlaggedAccount>>();
        mod.MapPost("/payout-accounts/{ownerId:long}/review", ReviewAsync).Validate<ReviewRequest>();
    }

    /// <summary>Digits only: spaces, dots and dashes people type are dropped (same rule as bank_account identifiers).</summary>
    public static string? NormalizeAccount(string? value)
    {
        if (value is null) return null;
        var digits = new string(value.Where(c => c is not (' ' or '.' or '-')).ToArray());
        return digits.Length > 0 && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    /// <summary>"Nguyễn Văn  Đức" → "NGUYEN VAN DUC": uppercase, no diacritics, single spaces (as banks print it).</summary>
    public static string? NormalizeHolder(string? value)
    {
        if (value is null) return null;
        var decomposed = value.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var plain = new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return string.Join(' ', plain.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static byte[] AssociatedData(long ownerId) => Encoding.UTF8.GetBytes($"payout-account:{ownerId}");

    public static async Task<IResult> GetAsync(HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return Results.Ok(await ReadAsync(connection, CurrentUser.Id(http.User), ct) ?? throw new ProblemException("resource.not_found"));
    }

    /// <summary>
    /// Saving the same number keeps the status and the last review; a new number resets the review and is checked
    /// against confirmed reports (BR-31): a match flags the account until a moderator reviews it.
    /// </summary>
    public static async Task<IResult> PutAsync(
        PayoutRequest request, HttpContext http, NpgsqlDataSource db, DocumentCipher cipher, IdentifierHasher hasher,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var owner = CurrentUser.Id(http.User);
        var number = NormalizeAccount(request.AccountNumber)!;
        var hmac = hasher.Hash("bank_account", number);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var bankActive = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM app.banks WHERE id = @Id AND is_active)", new { Id = request.BankId }, tx, cancellationToken: ct));
        if (!bankActive)
            throw new ProblemException("validation.failed", errors: new Dictionary<string, string[]> { ["bankId"] = ["Chọn ngân hàng"] });

        var reported = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM app.identifiers i JOIN app.identifier_stats s ON s.identifier_id = i.id
                           WHERE i.kind = 'bank_account' AND i.value_hmac = @Hmac AND s.confirmed_report_count > 0)
            """, new { Hmac = hmac }, tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO app.payout_accounts (owner_id, bank_id, account_ciphertext, key_id, account_hmac, account_hint,
                                             holder_name, status)
            VALUES (@Owner, @BankId, @Cipher, @KeyId, @Hmac, @Hint, @Holder, @Status::app.payout_account_status)
            ON CONFLICT (owner_id) DO UPDATE SET
                bank_id = EXCLUDED.bank_id,
                account_ciphertext = EXCLUDED.account_ciphertext,
                key_id = EXCLUDED.key_id,
                holder_name = EXCLUDED.holder_name,
                status = CASE WHEN payout_accounts.account_hmac = EXCLUDED.account_hmac THEN payout_accounts.status
                              ELSE EXCLUDED.status END,
                reviewed_by = CASE WHEN payout_accounts.account_hmac = EXCLUDED.account_hmac THEN payout_accounts.reviewed_by END,
                reviewed_at = CASE WHEN payout_accounts.account_hmac = EXCLUDED.account_hmac THEN payout_accounts.reviewed_at END,
                review_note = CASE WHEN payout_accounts.account_hmac = EXCLUDED.account_hmac THEN payout_accounts.review_note END,
                account_hmac = EXCLUDED.account_hmac,
                account_hint = EXCLUDED.account_hint
            """, new
            {
                Owner = owner,
                request.BankId,
                Cipher = cipher.Encrypt(Encoding.UTF8.GetBytes(number), AssociatedData(owner)),
                KeyId = cipher.ActiveKeyId,
                Hmac = hmac,
                Hint = "•••" + number[^4..],
                Holder = NormalizeHolder(request.HolderName),
                Status = reported ? "flagged" : "active",
            }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);

        var saved = (await ReadAsync(connection, owner, ct))!;
        if (saved.Status == "flagged")
            loggers.CreateLogger("PayoutAccounts").LogWarning("Payout account flagged for review: owner {UserId}", owner);
        return Results.Ok(saved);
    }

    public static async Task<IResult> ListFlaggedAsync(
        NpgsqlDataSource db, CancellationToken ct, string status = "flagged", int page = 1, int pageSize = 20)
    {
        if (status is not ("flagged" or "active"))
            throw new ProblemException("validation.failed", errors: new Dictionary<string, string[]> { ["status"] = ["flagged hoặc active"] });
        (page, pageSize) = (Math.Max(page, 1), Math.Clamp(pageSize, 1, 50));
        await using var connection = await db.OpenConnectionAsync(ct);
        var args = new { Status = status, Limit = pageSize, Offset = (page - 1) * pageSize };
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM app.payout_accounts WHERE status = @Status::app.payout_account_status", args, cancellationToken: ct));
        var items = await connection.QueryAsync<FlaggedAccount>(new CommandDefinition(
            """
            SELECT pa.owner_id, u.full_name AS owner_name, b.short_name AS bank_short_name, pa.account_hint, pa.holder_name,
                   pa.status::text AS status,
                   coalesce((SELECT max(s.confirmed_report_count) FROM app.identifiers i
                             JOIN app.identifier_stats s ON s.identifier_id = i.id
                             WHERE i.kind = 'bank_account' AND i.value_hmac = pa.account_hmac), 0) AS confirmed_report_count,
                   pa.updated_at, pa.reviewed_at, pa.review_note
            FROM app.payout_accounts pa
            JOIN app.users u ON u.id = pa.owner_id
            JOIN app.banks b ON b.id = pa.bank_id
            WHERE pa.status = @Status::app.payout_account_status
            ORDER BY pa.updated_at
            LIMIT @Limit OFFSET @Offset
            """, args, cancellationToken: ct));
        return Results.Ok(new Paged<FlaggedAccount>(items.ToList(), page, pageSize, total));
    }

    /// <summary>US-1004: clear → active; keep → stays flagged. The decision and note are kept on the row.</summary>
    public static async Task<IResult> ReviewAsync(long ownerId, ReviewRequest request, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var moderator = CurrentUser.Id(http.User);
        if (ownerId == moderator) throw new ProblemException("moderation.conflict_of_interest");
        await using var connection = await db.OpenConnectionAsync(ct);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE app.payout_accounts
            SET status = CASE WHEN @Decision = 'clear' THEN 'active'::app.payout_account_status ELSE 'flagged' END,
                reviewed_by = @Moderator, reviewed_at = now(), review_note = @Note
            WHERE owner_id = @Owner
            """, new { request.Decision, Moderator = moderator, Note = request.Note.Trim(), Owner = ownerId }, cancellationToken: ct));
        return updated == 0 ? throw new ProblemException("resource.not_found") : Results.NoContent();
    }

    private static Task<PayoutResponse?> ReadAsync(NpgsqlConnection connection, long owner, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<PayoutResponse>(new CommandDefinition(
            """
            SELECT pa.bank_id, b.short_name AS bank_short_name, pa.account_hint, pa.holder_name, pa.status::text AS status
            FROM app.payout_accounts pa JOIN app.banks b ON b.id = pa.bank_id
            WHERE pa.owner_id = @Owner
            """, new { Owner = owner }, cancellationToken: ct));
}

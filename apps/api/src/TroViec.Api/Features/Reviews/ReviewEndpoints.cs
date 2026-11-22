using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Features.Listings;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Reviews;

public sealed record ReviewRequest(int Rating, string? Body);

public sealed record HideRequest(string Reason);

public sealed class ReviewValidator : AbstractValidator<ReviewRequest>
{
    public ReviewValidator()
    {
        RuleFor(r => r.Rating).InclusiveBetween(1, 5).WithMessage("Chấm từ 1 đến 5 sao");
        RuleFor(r => r.Body).Must(b => b is null || b.Trim().Length is >= 1 and <= 1000).WithMessage("Nhận xét tối đa 1.000 ký tự");
    }
}

public sealed class HideValidator : AbstractValidator<HideRequest>
{
    public HideValidator() => RuleFor(r => r.Reason).Must(r => r is not null && r.Trim().Length is >= 1 and <= 300)
        .WithMessage("Nhập lý do (tối đa 300 ký tự)");
}

/// <summary>Reviews (US-405, US-608; BR-15) and saved listings (US-404).</summary>
public static class ReviewEndpoints
{
    public sealed record Review(long Id, string ReviewerName, int Rating, string? Body, DateTime CreatedAt);

    public sealed record Created(long Id);

    /// <summary>A saved listing; state "unavailable" carries no content (hidden, removed, back to draft…).</summary>
    public sealed record SavedItem(Guid ListingId, string State, DateTime SavedAt, string? Title, string? ListingType, string? CoverImageUrl);

    public static void MapReviewsAndSaved(this IEndpointRouteBuilder app)
    {
        var listings = app.MapGroup("/api/v1/listings/{listingId:guid}/reviews").WithTags("Listings");
        listings.MapGet("/", ListAsync).AllowAnonymous().Produces<List<Review>>();
        listings.MapPost("/", CreateAsync).RequireAuthorization(Permissions.ReviewCreate).Validate<ReviewRequest>()
            .Produces<Created>(StatusCodes.Status201Created);
        app.MapPost("/api/v1/moderation/reviews/{id:long}/hide", HideAsync).WithTags("Moderation")
            .RequireAuthorization(Permissions.ReviewModerate).Validate<HideRequest>();

        var saved = app.MapGroup("/api/v1/me/saved-listings").WithTags("Me").RequireAuthorization(Permissions.ListingSave);
        saved.MapGet("/", SavedAsync).Produces<List<SavedItem>>();
        saved.MapPut("/{listingId:guid}", SaveAsync);
        saved.MapDelete("/{listingId:guid}", UnsaveAsync);
    }

    /// <summary>Reviews are shown and accepted on public or expired listings only.</summary>
    private static async Task<long> ReviewableListingAsync(NpgsqlConnection connection, Guid listingId, CancellationToken ct) =>
        await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            """
            SELECT id FROM app.listings
            WHERE public_id = @Id AND ((status = 'verified' AND expires_at > now()) OR status = 'expired'
                                       OR (status = 'verified' AND expires_at <= now()))
            """, new { Id = listingId }, cancellationToken: ct)) ?? throw new ProblemException("listing.not_found");

    public static async Task<IResult> ListAsync(Guid listingId, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var id = await ReviewableListingAsync(connection, listingId, ct);
        return Results.Ok((await connection.QueryAsync<Review>(new CommandDefinition(
            """
            SELECT r.id, u.full_name AS reviewer_name, r.rating::int AS rating, r.body, r.created_at
            FROM app.reviews r JOIN app.users u ON u.id = r.reviewer_id
            WHERE r.listing_id = @Id AND r.status = 'published'
            ORDER BY r.created_at DESC
            """, new { Id = id }, cancellationToken: ct))).ToList());
    }

    /// <summary>The trigger check_review answers review.not_student / review.own_listing; UNIQUE → review.already_exists.</summary>
    public static async Task<IResult> CreateAsync(Guid listingId, ReviewRequest request, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var listing = await ReviewableListingAsync(connection, listingId, ct);
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO app.reviews (listing_id, reviewer_id, rating, body) VALUES (@Listing, @User, @Rating, @Body) RETURNING id",
            new { Listing = listing, User = CurrentUser.Id(http.User), Rating = (short)request.Rating, Body = request.Body?.Trim() },
            cancellationToken: ct));
        return Results.Created($"/api/v1/listings/{listingId}/reviews", new Created(id));
    }

    public static async Task<IResult> HideAsync(long id, HideRequest request, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var moderator = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        var review = await connection.QuerySingleOrDefaultAsync<(long Reviewer, long Poster, string Status)?>(new CommandDefinition(
            """
            SELECT r.reviewer_id, l.poster_id, r.status::text FROM app.reviews r JOIN app.listings l ON l.id = r.listing_id
            WHERE r.id = @Id
            """, new { Id = id }, cancellationToken: ct)) ?? throw new ProblemException("resource.not_found");
        if (review.Reviewer == moderator || review.Poster == moderator) throw new ProblemException("moderation.conflict_of_interest");
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE app.reviews SET status = 'hidden', hider_id = @Moderator, hidden_reason = @Reason
            WHERE id = @Id AND status = 'published'
            """, new { Id = id, Moderator = moderator, Reason = request.Reason.Trim() }, cancellationToken: ct));
        return Results.NoContent();
    }

    public static async Task<IResult> SavedAsync(HttpContext http, NpgsqlDataSource db, MediaUrls urls, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<(Guid PublicId, string State, DateTime SavedAt, string Title, string Type, string? Cover)>(
            new CommandDefinition(
                """
                SELECT l.public_id,
                       CASE WHEN l.status = 'verified' AND l.expires_at > now() THEN 'public'
                            WHEN l.status = 'expired' OR (l.status = 'verified' AND l.expires_at <= now()) THEN 'expired'
                            ELSE 'unavailable' END,
                       s.saved_at, l.title, l.listing_type::text,
                       (SELECT i.object_key FROM app.listing_images i WHERE i.listing_id = l.id ORDER BY i.sort_order LIMIT 1)
                FROM app.saved_listings s JOIN app.listings l ON l.id = s.listing_id
                WHERE s.user_id = @UserId
                ORDER BY s.saved_at DESC
                """, new { UserId = CurrentUser.Id(http.User) }, cancellationToken: ct));
        return Results.Ok(rows.Select(r => r.State == "unavailable"
            ? new SavedItem(r.PublicId, r.State, r.SavedAt, null, null, null)
            : new SavedItem(r.PublicId, r.State, r.SavedAt, r.Title, r.Type, r.Cover is null ? null : urls.ListingImage(r.Cover))).ToList());
    }

    /// <summary>Idempotent; only a public listing can be saved.</summary>
    public static async Task<IResult> SaveAsync(Guid listingId, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var listing = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT id FROM app.listings WHERE public_id = @Id AND status = 'verified' AND expires_at > now()",
            new { Id = listingId }, cancellationToken: ct)) ?? throw new ProblemException("listing.not_found");
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO app.saved_listings (user_id, listing_id) VALUES (@User, @Listing) ON CONFLICT DO NOTHING",
            new { User = CurrentUser.Id(http.User), Listing = listing }, cancellationToken: ct));
        return Results.NoContent();
    }

    public static async Task<IResult> UnsaveAsync(Guid listingId, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM app.saved_listings
            WHERE user_id = @User AND listing_id = (SELECT id FROM app.listings WHERE public_id = @Id)
            """, new { User = CurrentUser.Id(http.User), Id = listingId }, cancellationToken: ct));
        return Results.NoContent();
    }
}

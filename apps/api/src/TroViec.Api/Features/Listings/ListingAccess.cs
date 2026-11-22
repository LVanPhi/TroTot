using Dapper;
using Npgsql;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Listings;

public sealed record OwnedListing(long Id, Guid PublicId, long PosterId, string ListingType, string Status, int Version);

/// <summary>Ownership checks for poster endpoints (AGENTS.md rule 12, docs/04-api.md "chủ tin").</summary>
public static class ListingAccess
{
    /// <summary>
    /// The caller's own listing, locked FOR UPDATE inside <paramref name="tx"/>. Someone else's listing → 404, except a
    /// listing that is public anyway (verified and not expired) → 403 listing.not_owner (nothing hidden is revealed).
    /// </summary>
    public static async Task<OwnedListing> OwnedAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, Guid publicId,
        long userId, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<(long Id, long PosterId, string ListingType, string Status, int Version, bool IsPublic)?>(
            new CommandDefinition(
                $"""
                SELECT id, poster_id, listing_type::text, status::text, version,
                       status = 'verified' AND expires_at > now()
                FROM app.listings WHERE public_id = @PublicId
                {(tx is null ? "" : "FOR UPDATE")}
                """, new { PublicId = publicId }, tx, cancellationToken: ct));
        if (row is not { } l) throw new ProblemException("listing.not_found");
        if (l.PosterId != userId) throw new ProblemException(l.IsPublic ? "listing.not_owner" : "listing.not_found");
        return new OwnedListing(l.Id, publicId, l.PosterId, l.ListingType, l.Status, l.Version);
    }

    /// <summary>Content and photos change only in draft (US-303, US-307).</summary>
    public static void RequireDraft(OwnedListing listing)
    {
        if (listing.Status != "draft") throw new ProblemException("listing.not_editable");
    }

    public static void RequireVersion(OwnedListing listing, int version)
    {
        if (listing.Version != version) throw new ProblemException("listing.version_conflict");
    }
}

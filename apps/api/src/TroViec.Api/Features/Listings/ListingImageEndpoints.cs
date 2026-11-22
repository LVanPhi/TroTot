using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Infrastructure.Storage;

namespace TroViec.Api.Features.Listings;

public sealed record CreatedImage(long ImageId, string Url, int SortOrder);

/// <summary>Public URL of a listing photo: S3__PublicMediaBaseUrl + object key (Caddy serves the bucket).</summary>
public sealed class MediaUrls(IConfiguration configuration)
{
    private string Base => (configuration["S3:PublicMediaBaseUrl"] ?? "/media/listing-images").TrimEnd('/');

    public string ListingImage(string objectKey) => $"{Base}/{objectKey}";
}

/// <summary>Listing photos, drafts only (US-303): add, delete, reorder. At most 10; the same file twice → image.duplicate.</summary>
public static class ListingImageEndpoints
{
    public const int MaxImages = 10;

    public static async Task<IResult> AddAsync(Guid listingId, HttpContext http, NpgsqlDataSource db, IObjectStorage storage,
        IOptions<StorageBuckets> buckets, MediaUrls urls, CancellationToken ct)
    {
        var userId = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        // Cheap checks before reading the upload.
        var listing = await ListingAccess.OwnedAsync(connection, null, listingId, userId, ct);
        ListingAccess.RequireDraft(listing);

        if (!http.Request.HasFormContentType) throw Validation("file", "Chọn ảnh");
        IFormCollection form;
        try { form = await http.Request.ReadFormAsync(ct); }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { throw Validation("file", "Chọn ảnh"); }
        if (form.Files.GetFile("file") is not { Length: > 0 } file) throw Validation("file", "Chọn ảnh");
        if (file.Length > ImagePipeline.MaxBytes) throw new ProblemException("image.too_large");

        ProcessedImage image;
        await using (var stream = file.OpenReadStream()) image = await ImagePipeline.ProcessAsync(stream, ct);

        var bucket = buckets.Value.ListingImages;
        var key = ObjectKeys.ListingImage(listingId);
        await storage.PutAsync(bucket, key, image.Webp, "image/webp", ct);
        try
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            listing = await ListingAccess.OwnedAsync(connection, tx, listingId, userId, ct);   // lock and re-check
            ListingAccess.RequireDraft(listing);
            var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM app.listing_images WHERE listing_id = @Id", new { listing.Id }, tx, cancellationToken: ct));
            if (count >= MaxImages) throw new ProblemException("image.too_many");
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO app.listing_images (listing_id, object_key, sha256, width_px, height_px, sort_order)
                VALUES (@ListingId, @Key, @Sha, @Width, @Height, @Sort)
                RETURNING id
                """, new
                {
                    ListingId = listing.Id, Key = key, Sha = image.Sha256,
                    Width = (short)image.Width, Height = (short)image.Height, Sort = (short)(count + 1),
                }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/listings/{listingId}/images/{id}", new CreatedImage(id, urls.ListingImage(key), count + 1));
        }
        catch
        {
            await storage.DeleteAsync(bucket, key, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Deletes the photo and renumbers the rest 1..n (the sort constraint is DEFERRABLE).</summary>
    public static async Task<IResult> DeleteAsync(Guid listingId, long imageId, HttpContext http, NpgsqlDataSource db,
        IObjectStorage storage, IOptions<StorageBuckets> buckets, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var listing = await ListingAccess.OwnedAsync(connection, tx, listingId, CurrentUser.Id(http.User), ct);
        ListingAccess.RequireDraft(listing);
        var key = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "DELETE FROM app.listing_images WHERE id = @ImageId AND listing_id = @Id RETURNING object_key",
            new { ImageId = imageId, listing.Id }, tx, cancellationToken: ct)) ?? throw new ProblemException("resource.not_found");
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE app.listing_images i SET sort_order = r.n
            FROM (SELECT id, row_number() OVER (ORDER BY sort_order)::smallint AS n
                  FROM app.listing_images WHERE listing_id = @Id) r
            WHERE i.id = r.id AND i.sort_order <> r.n
            """, new { listing.Id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        await storage.DeleteAsync(buckets.Value.ListingImages, key, ct);
        return Results.NoContent();
    }

    /// <summary>imageIds must list every photo of the listing exactly once, in the new order.</summary>
    public static async Task<IResult> ReorderAsync(Guid listingId, ImageOrderRequest request, HttpContext http, NpgsqlDataSource db,
        CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var listing = await ListingAccess.OwnedAsync(connection, tx, listingId, CurrentUser.Id(http.User), ct);
        ListingAccess.RequireDraft(listing);
        var current = (await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT id FROM app.listing_images WHERE listing_id = @Id", new { listing.Id }, tx, cancellationToken: ct))).ToHashSet();
        var ids = request.ImageIds ?? [];
        if (ids.Count != current.Count || ids.Distinct().Count() != ids.Count || !ids.All(current.Contains))
            throw Validation("imageIds", "Danh sách ảnh phải gồm đúng mọi ảnh của tin, mỗi ảnh một lần");

        // uq_listing_images_listing_sort is DEFERRABLE INITIALLY DEFERRED: swaps are checked at commit.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE app.listing_images i SET sort_order = o.n
            FROM unnest(@Ids) WITH ORDINALITY AS o(id, n)
            WHERE i.id = o.id AND i.listing_id = @ListingId
            """, new { Ids = ids.ToArray(), ListingId = listing.Id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static ProblemException Validation(string field, string message) =>
        new("validation.failed", errors: new Dictionary<string, string[]> { [field] = [message] });
}

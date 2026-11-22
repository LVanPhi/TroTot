using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Infrastructure.Storage;

namespace TroViec.Api.Features.Listings;

/// <summary>Routes of the listing feature (docs/04-api.md, Tin đăng). "Chủ tin" = listing.create + poster_id = sub.</summary>
public static class ListingEndpoints
{
    public static IServiceCollection AddListings(this IServiceCollection services) => services
        .AddSingleton<MediaUrls>()
        // Read lazily: test configuration is applied after services are registered.
        .AddSingleton(sp => new Infrastructure.Crypto.IdentifierHasher(
            sp.GetRequiredService<IConfiguration>()["Crypto:IdentifierHmacKey"]
            ?? throw new InvalidOperationException("Crypto__IdentifierHmacKey is not set")));

    public static void MapListings(this IEndpointRouteBuilder app)
    {
        var listings = app.MapGroup("/api/v1/listings").WithTags("Listings");
        listings.MapGet("/{listingId:guid}", ListingReadEndpoints.DetailAsync).AllowAnonymous()
            .Produces<ListingReadEndpoints.Detail>();
        listings.MapGet("/{listingId:guid}/history", ListingReadEndpoints.HistoryAsync).RequireAuthorization()
            .Produces<List<ListingReadEndpoints.HistoryItem>>();

        var owner = listings.MapGroup("").RequireAuthorization(Permissions.ListingCreate);
        owner.MapPost("/", ListingWriteEndpoints.CreateAsync).Validate<ListingInput>()
            .Produces<CreatedListing>(StatusCodes.Status201Created);
        owner.MapPut("/{listingId:guid}", ListingWriteEndpoints.UpdateAsync).Validate<ListingUpdate>().Produces<VersionResponse>();
        owner.MapPost("/{listingId:guid}/images", ListingImageEndpoints.AddAsync)
            .DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ImagePipeline.MaxBytes + 64 * 1024))
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<CreatedImage>(StatusCodes.Status201Created);
        owner.MapDelete("/{listingId:guid}/images/{imageId:long}", ListingImageEndpoints.DeleteAsync);
        owner.MapPut("/{listingId:guid}/images/order", ListingImageEndpoints.ReorderAsync);
        owner.MapPost("/{listingId:guid}/submit", ListingLifecycleEndpoints.SubmitAsync).Validate<VersionRequest>()
            .Produces<LifecycleResponse>(StatusCodes.Status202Accepted);
        owner.MapPost("/{listingId:guid}/renew", ListingLifecycleEndpoints.RenewAsync).Validate<VersionRequest>()
            .Produces<LifecycleResponse>(StatusCodes.Status202Accepted);
        owner.MapPost("/{listingId:guid}/unpublish", ListingLifecycleEndpoints.UnpublishAsync).Validate<VersionRequest>()
            .Produces<LifecycleResponse>();
        owner.MapPost("/{listingId:guid}/remove", ListingLifecycleEndpoints.RemoveAsync).Validate<RemoveRequest>()
            .Produces<LifecycleResponse>();

        app.MapGet("/api/v1/me/listings", ListingReadEndpoints.MineAsync).WithTags("Me")
            .RequireAuthorization(Permissions.ListingCreate).Produces<TroViec.Api.Infrastructure.Db.Paged<ListingReadEndpoints.MyListing>>();
    }
}

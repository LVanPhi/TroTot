using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Infrastructure.Storage;

namespace TroViec.Api.Features.Listings;

/// <summary>Routes of the listing feature (docs/04-api.md, Tin đăng). "Chủ tin" = listing.create + poster_id = sub.</summary>
public static class ListingEndpoints
{
    public static IServiceCollection AddListings(this IServiceCollection services) => services.AddSingleton<MediaUrls>();

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

        app.MapGet("/api/v1/me/listings", ListingReadEndpoints.MineAsync).WithTags("Me")
            .RequireAuthorization(Permissions.ListingCreate).Produces<TroViec.Api.Infrastructure.Db.Paged<ListingReadEndpoints.MyListing>>();
    }
}

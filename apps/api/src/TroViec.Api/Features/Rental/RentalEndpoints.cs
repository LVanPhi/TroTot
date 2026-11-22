using TroViec.Api.Infrastructure.Auth;

namespace TroViec.Api.Features.Rental;

/// <summary>Rental management, phase 1 of the platform (docs/12-rental.md, API).</summary>
public static class RentalEndpoints
{
    public static void MapRental(this IEndpointRouteBuilder app)
    {
        var rental = app.MapGroup("/api/v1/rental").WithTags("Rental").RequireAuthorization(Permissions.RentalManage);
        rental.MapProperties();
        app.MapPayoutAccounts();
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TroViec.Api.Features.Rental;

/// <summary>
/// The canonical lease content and its SHA-256 (docs/12-rental.md, Mã băm hợp đồng; BR-24). Keys are sorted
/// alphabetically at every level, no extra whitespace, dates yyyy-MM-dd, money as integers, tenant emails lower-case
/// and sorted. System.Text.Json's default encoder escapes non-ASCII as \uXXXX, which keeps the bytes stable.
/// Changing anything here changes every hash: bump TemplateVersion instead and keep the old rules for old leases.
/// </summary>
public static class LeaseContent
{
    public const int TemplateVersion = 1;

    public sealed record Service(string Label, int AmountVnd);

    public sealed record Input(
        Guid LeaseId, string PropertyName, string StreetAddress, string WardCode, string RoomCode,
        string LandlordEmail, IReadOnlyList<string> TenantEmails,
        DateOnly StartDate, DateOnly EndDate, int RentVnd, int DepositVnd, short BillingDay,
        int ElectricityPriceVnd, int WaterPriceVnd, string WaterUnit, IReadOnlyList<Service> Services, string? ExtraTerms);

    public static string Canonical(Input i)
    {
        static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["billingDay"] = (int)i.BillingDay,
            ["depositVnd"] = i.DepositVnd,
            ["electricityPriceVnd"] = i.ElectricityPriceVnd,
            ["endDate"] = Date(i.EndDate),
            ["extraTerms"] = i.ExtraTerms,
            ["landlordEmail"] = i.LandlordEmail.Trim().ToLowerInvariant(),
            ["leaseId"] = i.LeaseId.ToString("D"),
            ["property"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = i.PropertyName,
                ["streetAddress"] = i.StreetAddress,
                ["wardCode"] = i.WardCode,
            },
            ["rentVnd"] = i.RentVnd,
            ["roomCode"] = i.RoomCode,
            ["services"] = i.Services.Select(s => new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["amountVnd"] = s.AmountVnd,
                ["label"] = s.Label,
            }).ToList(),
            ["startDate"] = Date(i.StartDate),
            ["templateVersion"] = TemplateVersion,
            ["tenantEmails"] = i.TenantEmails.Select(e => e.Trim().ToLowerInvariant()).Order(StringComparer.Ordinal).ToList(),
            ["waterPriceVnd"] = i.WaterPriceVnd,
            ["waterUnit"] = i.WaterUnit,
        };
        return JsonSerializer.Serialize(root);
    }

    public static byte[] Hash(Input input) => SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(input)));

    public static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);
}

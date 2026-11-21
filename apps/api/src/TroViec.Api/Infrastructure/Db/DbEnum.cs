using System.Text.Json;

namespace TroViec.Api.Infrastructure.Db;

/// <summary>
/// The single place that converts C# enums to PostgreSQL enum labels and back (docs/03-database.md, Kiểu dữ liệu).
/// PendingReview ↔ "pending_review". SQL must still cast: @Status::app.listing_status.
/// </summary>
public static class DbEnum
{
    public static string ToDb<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public static TEnum FromDb<TEnum>(string label) where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (ToDb(value) == label) return value;
        }
        throw new ArgumentOutOfRangeException(nameof(label), label, $"not a {typeof(TEnum).Name} label");
    }
}

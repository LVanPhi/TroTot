using System.Text.RegularExpressions;

namespace TroViec.Api.Domain;

/// <summary>
/// Vietnamese mobile number in E.164 (+84 followed by 9 digits, US-104 AC1). Accepts what people type:
/// "0912 345 678", "0912.345.678", "+84 912-345-678", "84912345678".
/// </summary>
public sealed partial record PhoneNumber
{
    private PhoneNumber(string e164) => E164 = e164;

    public string E164 { get; }

    public static bool TryParse(string? input, out PhoneNumber? phone)
    {
        phone = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var digits = Separators().Replace(input.Trim(), "");
        if (digits.StartsWith('+')) digits = digits[1..];
        else if (digits.StartsWith('0')) digits = "84" + digits[1..];

        if (!Mobile().IsMatch(digits)) return false;
        phone = new PhoneNumber("+" + digits);
        return true;
    }

    public override string ToString() => E164;

    [GeneratedRegex(@"[\s.\-()]")]
    private static partial Regex Separators();

    // 84 + 9 digits; national numbers starting 3, 5, 7, 8, 9 are mobile (landlines start with 2).
    [GeneratedRegex("^84[35789][0-9]{8}$")]
    private static partial Regex Mobile();
}

using FluentValidation;

namespace TroViec.Api.Features.Listings;

public sealed record GeoPoint(double Lat, double Lng);

public sealed record RoomInput(int MonthlyRentVnd, int? DepositVnd, decimal AreaM2, bool IsSelfContained, IReadOnlyList<string>? AmenityCodes);

public sealed record JobInput(string EmployerName, int PayVnd, string PayUnit, short HoursPerWeek);

/// <summary>POST /listings body (docs/04-api.md, Tin đăng — người đăng).</summary>
public sealed record ListingInput(
    string ListingType, string Title, string Description, string WardCode, string StreetAddress, GeoPoint Location,
    RoomInput? Room, JobInput? Job);

/// <summary>PUT /listings/{id}: the same fields plus the version the client edited.</summary>
public sealed record ListingUpdate(
    string ListingType, string Title, string Description, string WardCode, string StreetAddress, GeoPoint Location,
    RoomInput? Room, JobInput? Job, int Version)
{
    public ListingInput Input => new(ListingType, Title, Description, WardCode, StreetAddress, Location, Room, Job);
}

public sealed record CreatedListing(Guid ListingId, int Version);

public sealed record VersionResponse(int Version);

/// <summary>Mirrors the CHECK constraints of listings / room_listings / job_listings (0001_init.sql).</summary>
public static class ListingRules
{
    public static readonly string[] PayUnits = ["hour", "shift", "day", "month"];

    public static void Apply<T>(AbstractValidator<T> v, Func<T, ListingInput> input)
    {
        v.RuleFor(x => input(x).ListingType).Must(t => t is "room" or "job").OverridePropertyName("listingType")
            .WithMessage("Loại tin là phòng hoặc việc làm");
        v.RuleFor(x => input(x).Title).Must(t => t is not null && t.Trim().Length is >= 1 and <= 150)
            .OverridePropertyName("title").WithMessage("Tiêu đề từ 1 đến 150 ký tự");
        v.RuleFor(x => input(x).Description).Must(d => d is not null && d.Trim().Length >= 30 && d.Length <= 5000)
            .OverridePropertyName("description").WithMessage("Mô tả từ 30 đến 5.000 ký tự");
        v.RuleFor(x => input(x).WardCode).Must(w => w is not null && w.Length == 5 && w.All(char.IsAsciiDigit))
            .OverridePropertyName("wardCode").WithMessage("Chọn phường/xã");
        v.RuleFor(x => input(x).StreetAddress).Must(a => a is not null && a.Trim().Length is >= 1 and <= 200)
            .OverridePropertyName("streetAddress").WithMessage("Địa chỉ từ 1 đến 200 ký tự");
        v.RuleFor(x => input(x).Location).Must(l => l is { Lat: >= 8 and <= 24, Lng: >= 102 and <= 110 })
            .OverridePropertyName("location").WithMessage("Ghim vị trí trong lãnh thổ Việt Nam");
        v.RuleFor(x => input(x)).Must(i => (i.ListingType == "room" && i.Room is not null && i.Job is null)
                                         || (i.ListingType == "job" && i.Job is not null && i.Room is null))
            .OverridePropertyName("listingType").WithMessage("Điền đúng phần phòng hoặc việc làm theo loại tin");

        v.When(x => input(x).Room is not null, () =>
        {
            v.RuleFor(x => input(x).Room!.MonthlyRentVnd).InclusiveBetween(100_000, 100_000_000)
                .OverridePropertyName("room.monthlyRentVnd").WithMessage("Giá thuê từ 100.000 đến 100.000.000 đồng");
            v.RuleFor(x => input(x).Room!.DepositVnd).Must(d => d is null or >= 0)
                .OverridePropertyName("room.depositVnd").WithMessage("Tiền cọc không âm");
            v.RuleFor(x => input(x).Room!.AreaM2).Must(a => a >= 0.1m && a <= 500 && decimal.Round(a, 1) == a)
                .OverridePropertyName("room.areaM2").WithMessage("Diện tích từ 0,1 đến 500 m², một chữ số thập phân");
            v.RuleFor(x => input(x).Room!.AmenityCodes).Must(c => c is null || (c.Count <= 30 && c.Distinct().Count() == c.Count))
                .OverridePropertyName("room.amenityCodes").WithMessage("Tiện nghi bị trùng");
        });
        v.When(x => input(x).Job is not null, () =>
        {
            v.RuleFor(x => input(x).Job!.EmployerName).Must(n => n is not null && n.Trim().Length is >= 1 and <= 200)
                .OverridePropertyName("job.employerName").WithMessage("Tên nơi làm việc từ 1 đến 200 ký tự");
            v.RuleFor(x => input(x).Job!.PayVnd).InclusiveBetween(1, 100_000_000)
                .OverridePropertyName("job.payVnd").WithMessage("Mức lương từ 1 đến 100.000.000 đồng");
            v.RuleFor(x => input(x).Job!.PayUnit).Must(u => PayUnits.Contains(u))
                .OverridePropertyName("job.payUnit").WithMessage("Đơn vị lương: giờ, buổi, ngày hoặc tháng");
            v.RuleFor(x => input(x).Job!.HoursPerWeek).InclusiveBetween((short)1, (short)48)
                .OverridePropertyName("job.hoursPerWeek").WithMessage("Số giờ mỗi tuần từ 1 đến 48");
        });
    }
}

public sealed class ListingInputValidator : AbstractValidator<ListingInput>
{
    public ListingInputValidator() => ListingRules.Apply(this, x => x);
}

public sealed class ListingUpdateValidator : AbstractValidator<ListingUpdate>
{
    public ListingUpdateValidator()
    {
        ListingRules.Apply(this, x => x.Input);
        RuleFor(x => x.Version).GreaterThan(0).WithMessage("Thiếu phiên bản tin");
    }
}

public sealed record ImageOrderRequest(IReadOnlyList<long> ImageIds);

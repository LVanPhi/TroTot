using Microsoft.AspNetCore.Authorization;

namespace TroViec.Api.Infrastructure.Auth;

/// <summary>
/// Permission codes (app.permissions, seeded by 0002 and 0005). Each code is also an authorization policy name that requires
/// a matching "perm" claim, so endpoints say RequireAuthorization(Permissions.UserManage). The API checks
/// permissions, never role names (docs/01-requirements.md, Ma trận phân quyền).
/// </summary>
public static class Permissions
{
    public const string ScanCreate = "scan.create";
    public const string ListingCreate = "listing.create";
    public const string ListingSave = "listing.save";
    public const string ListingAppeal = "listing.appeal";
    public const string ReportCreate = "report.create";
    public const string ReviewCreate = "review.create";
    public const string ListingModerate = "listing.moderate";
    public const string ReportResolve = "report.resolve";
    public const string AppealResolve = "appeal.resolve";
    public const string ReviewModerate = "review.moderate";
    public const string OwnerVerify = "owner.verify";
    public const string DocumentView = "document.view";
    public const string AuditViewOwn = "audit.view_own";
    public const string AuditViewAll = "audit.view_all";
    public const string CriteriaManage = "criteria.manage";
    public const string ModelManage = "model.manage";
    public const string UserManage = "user.manage";
    public const string StatsView = "stats.view";

    // Quản lý trọ (0005, docs/12-rental.md).
    public const string RentalManage = "rental.manage";
    public const string RentalTenant = "rental.tenant";

    public static IReadOnlyList<string> All { get; } = typeof(Permissions)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToList();

    public static void AddPermissionPolicies(this AuthorizationOptions options)
    {
        foreach (var code in All)
            options.AddPolicy(code, policy => policy.RequireAuthenticatedUser().RequireClaim(TokenService.PermissionClaim, code));
    }
}

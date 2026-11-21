using Npgsql;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Infrastructure.Db;

/// <summary>PostgreSQL errors → API error codes, per docs/03-database.md (Lỗi CSDL → mã lỗi API).</summary>
public static class PgErrorMapper
{
    private static readonly Dictionary<string, string> UniqueViolations = new(StringComparer.Ordinal)
    {
        ["uq_users_email"] = "auth.email_taken",
        ["uq_users_phone_e164"] = "auth.phone_taken",
        ["uq_listing_images_listing_sha256"] = "image.duplicate",
        ["uq_reports_open_listing_reporter"] = "report.already_open",
        ["uq_reports_open_scan_reporter"] = "report.already_open",
        ["uq_reviews_listing_reviewer"] = "review.already_exists",
        ["uq_appeals_one_open"] = "appeal.already_open",
        ["uq_appeals_listing_transition_id"] = "appeal.already_used",
        ["uq_owner_documents_sha256"] = "document.duplicate",
        ["uq_model_versions_one_active"] = "model.activation_conflict",
    };

    /// <summary>True for serialization failures and deadlocks, which callers may retry once.</summary>
    public static bool IsTransient(PostgresException ex) =>
        ex.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;

    /// <summary>
    /// Returns the matching ProblemException, or null when the error is not a known business case
    /// (the caller then lets it surface as a 500).
    /// </summary>
    public static ProblemException? Map(PostgresException ex, bool includeConstraintName)
    {
        switch (ex.SqlState)
        {
            case PostgresErrorCodes.UniqueViolation
                when ex.ConstraintName is { } name && UniqueViolations.TryGetValue(name, out var code):
                return new ProblemException(code, inner: ex);
            case PostgresErrorCodes.ForeignKeyViolation:
                return new ProblemException("validation.reference_not_found", inner: ex);
            case PostgresErrorCodes.CheckViolation:
                return new ProblemException(
                    "validation.constraint_violated",
                    extensions: includeConstraintName ? new Dictionary<string, object?> { ["constraint"] = ex.ConstraintName } : null,
                    inner: ex);
            case PostgresErrorCodes.RaiseException when ErrorCatalog.IsKnown(ex.MessageText):
                return new ProblemException(ex.MessageText, inner: ex);
            case PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected:
                return new ProblemException("system.retry_later", inner: ex);
            default:
                return null;
        }
    }
}

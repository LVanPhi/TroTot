namespace TroViec.Api.Infrastructure.Problems;

public sealed record ErrorInfo(int Status, string Title);

/// <summary>Error code → HTTP status and Vietnamese title, copied from docs/04-api.md (Lỗi).</summary>
public static class ErrorCatalog
{
    public const string TypeBaseUri = "https://troviec.example.com/errors/";

    private static readonly Dictionary<string, ErrorInfo> Entries = new(StringComparer.Ordinal)
    {
        ["validation.failed"] = new(400, "Dữ liệu chưa hợp lệ"),
        ["validation.reference_not_found"] = new(422, "Dữ liệu tham chiếu không tồn tại"),
        ["validation.constraint_violated"] = new(422, "Dữ liệu không thỏa ràng buộc"),
        ["auth.unauthenticated"] = new(401, "Vui lòng đăng nhập"),
        ["auth.forbidden"] = new(403, "Bạn không có quyền thực hiện thao tác này"),
        ["auth.invalid_credentials"] = new(401, "Email hoặc mật khẩu không đúng"),
        ["auth.locked_temporarily"] = new(423, "Thử lại sau 15 phút"),
        ["auth.account_locked"] = new(403, "Tài khoản đã bị khóa"),
        ["auth.email_not_verified"] = new(403, "Vui lòng xác thực email"),
        ["auth.email_taken"] = new(409, "Email đã được đăng ký"),
        ["auth.student_email_domain"] = new(422, "Dùng email do trường cấp"),
        ["auth.weak_password"] = new(422, "Mật khẩu cần ít nhất 10 ký tự và không quá phổ biến"),
        ["auth.code_invalid"] = new(422, "Mã không đúng hoặc đã hết hạn"),
        ["auth.code_attempts_exceeded"] = new(429, "Xin mã mới"),
        ["auth.phone_taken"] = new(409, "Số điện thoại đã được dùng"),
        ["phone.sms_unavailable"] = new(503, "Hệ thống chưa gửi được SMS; liên hệ quản trị viên để được xác thực số"),
        ["auth.refresh_invalid"] = new(401, "Phiên đăng nhập đã hết"),
        ["auth.refresh_reused"] = new(401, "Phiên bị thu hồi vì lý do an toàn"),
        ["scan.input_required"] = new(422, "Dán nội dung hoặc tải ít nhất một ảnh"),
        ["scan.quota_exceeded"] = new(429, "Bạn đã dùng hết lượt hôm nay"),
        ["scan.not_found"] = new(404, "Không tìm thấy lượt kiểm tra"),
        ["image.unsupported_type"] = new(415, "Chỉ nhận ảnh JPEG, PNG, WebP"),
        ["image.too_large"] = new(413, "Ảnh tối đa 5 MB"),
        ["image.too_many"] = new(422, "Quá số ảnh cho phép"),
        ["image.duplicate"] = new(409, "Ảnh này đã có trong tin"),
        ["document.unsupported_type"] = new(415, "Chỉ nhận ảnh hoặc PDF"),
        ["document.too_large"] = new(413, "Tệp tối đa 10 MB"),
        ["resource.not_found"] = new(404, "Không tìm thấy"),
        ["listing.not_found"] = new(404, "Không tìm thấy tin"),
        ["listing.not_owner"] = new(403, "Bạn không phải người đăng tin này"),
        ["listing.version_conflict"] = new(409, "Tin vừa được cập nhật ở nơi khác"),
        ["listing.invalid_transition"] = new(409, "Không thể thực hiện ở trạng thái hiện tại"),
        ["listing.not_editable"] = new(409, "Chuyển tin về nháp trước khi sửa"),
        ["listing.poster_not_verified"] = new(403, "Hoàn tất xác minh để gửi duyệt"),
        ["listing.images_required"] = new(422, "Thêm ít nhất một ảnh"),
        ["listing.renew_too_early"] = new(409, "Chỉ gia hạn khi tin sắp hết hạn"),
        ["report.already_open"] = new(409, "Bạn đã báo cáo mục này"),
        ["report.already_resolved"] = new(409, "Báo cáo này đã được xử lý"),
        ["appeal.already_resolved"] = new(409, "Kháng nghị này đã được xử lý"),
        ["review.already_exists"] = new(409, "Bạn đã đánh giá tin này"),
        ["review.not_student"] = new(403, "Chỉ sinh viên được đánh giá"),
        ["review.own_listing"] = new(403, "Không thể đánh giá tin của chính mình"),
        ["appeal.already_open"] = new(409, "Đã có kháng nghị đang chờ xử lý"),
        ["appeal.already_used"] = new(409, "Mỗi quyết định chỉ kháng nghị một lần"),
        ["appeal.pending"] = new(409, "Tin đang chờ xử lý kháng nghị"),
        ["appeal.window_closed"] = new(409, "Đã quá hạn kháng nghị"),
        ["appeal.target_invalid"] = new(409, "Không có quyết định nào để kháng nghị"),
        ["appeal.decision_superseded"] = new(409, "Quyết định này không còn hiệu lực"),
        ["appeal.decider_conflict"] = new(403, "Kháng nghị phải do cán bộ khác xử lý"),
        ["moderation.conflict_of_interest"] = new(403, "Việc này phải do cán bộ khác xử lý"),
        ["owner.profile_required"] = new(409, "Tạo hồ sơ trước"),
        ["owner.no_accepted_document"] = new(409, "Duyệt ít nhất một giấy tờ trước"),
        ["me.listing_in_screening"] = new(409, "Có tin đang được sàng lọc, thử lại sau ít phút"),
        ["document.consent_required"] = new(422, "Cần đồng ý xử lý dữ liệu"),
        ["document.duplicate"] = new(409, "Tệp này đã được tải lên"),
        ["document.token_invalid"] = new(403, "Link đã hết hạn"),
        ["document.erased"] = new(410, "Tệp đã bị xóa"),
        ["model.activation_conflict"] = new(409, "Đã có phiên bản khác đang chạy"),
        ["model.not_implemented"] = new(409, "Worker chưa có cài đặt cho phiên bản này"),
        ["criteria.code_taken"] = new(409, "Mã tiêu chí đã tồn tại"),
        ["admin.cannot_remove_own_admin"] = new(409, "Không thể tự thu hồi vai trò quản trị"),
        ["admin.cannot_lock_self"] = new(409, "Không thể tự khóa tài khoản của mình"),
        ["rate_limited"] = new(429, "Thao tác quá nhanh"),
        ["request.too_large"] = new(413, "Dữ liệu gửi lên quá lớn"),
        ["system.retry_later"] = new(503, "Thử lại sau giây lát"),
        ["system.error"] = new(500, "Có lỗi xảy ra, vui lòng thử lại"),
    };

    public static bool IsKnown(string code) => Entries.ContainsKey(code);

    /// <summary>Unknown codes are a programming error; surface them as 500 rather than inventing a status.</summary>
    public static ErrorInfo Get(string code) =>
        Entries.TryGetValue(code, out var info) ? info : Entries["system.error"];
}

/**
 * Safety advice per criterion code (docs/06-frontend.md, Hiển thị kết quả kiểm tra, mục 4).
 * Criteria an admin adds later have no entry yet and get the general advice.
 */
export const GENERAL_TIP = "Kiểm tra trực tiếp trước khi chuyển tiền hoặc gửi giấy tờ.";

const tips: Record<string, string> = {
  upfront_fee: "Việc làm hợp pháp không thu phí trước. Không nộp tiền hồ sơ, đồng phục hay đặt cọc để được nhận việc.",
  deposit_before_viewing:
    "Chỉ đặt cọc sau khi đã xem phòng và có giấy biên nhận ghi rõ thông tin hai bên.",
  pressure: "Đừng để bị hối thúc. Dành thời gian xem phòng, hỏi người quen và so sánh trước khi quyết định.",
  vague_high_pay: "Mức lương cao bất thường cho việc nhẹ thường đi kèm yêu cầu nạp tiền. Hỏi rõ công việc và địa chỉ làm việc.",
  chat_only_no_address: "Yêu cầu địa chỉ cụ thể và gặp trực tiếp ở nơi làm việc trước khi nhận việc.",
  requests_sensitive_info: "Không gửi ảnh căn cước, mã OTP hay mật khẩu cho người lạ qua tin nhắn.",
  prompt_injection: "Nội dung có câu tìm cách điều khiển hệ thống chấm điểm. Hãy cẩn trọng hơn với tin này.",
};

export function tipFor(code: string): string {
  return tips[code] ?? GENERAL_TIP;
}

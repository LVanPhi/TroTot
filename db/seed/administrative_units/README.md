# Nạp danh mục đơn vị hành chính

Bảng `app.administrative_units` cần có trước khi tạo tin (khóa ngoại `listings.ward_code`).
Dữ liệu: 34 đơn vị cấp tỉnh (mã 2 chữ số) và 3.321 đơn vị cấp xã (mã 5 chữ số) áp dụng từ 01/7/2025
theo Quyết định 19/2025/QĐ-TTg; danh sách mã cấp xã chính thức do Cục Thống kê công bố
(Công văn 1027/CTK-CSCL năm 2025). Khi đơn vị đổi tên, mã số không đổi.

## Định dạng CSV đầu vào

UTF-8, có dòng tiêu đề, đúng 4 cột:

```csv
code,name,unit_level,parent_code
01,Thành phố Hà Nội,province,
<mã xã 5 số>,<Phường/Xã ...>,commune,01
```

- `unit_level`: `province` hoặc `commune`; tỉnh để trống `parent_code`.
- Tên ghi đủ loại đơn vị ("Phường …", "Xã …", "Đặc khu …") để hiển thị trực tiếp.

## Nguồn dữ liệu

1. **Chính thức**: phụ lục danh sách và mã số đơn vị hành chính của Cục Thống kê / Quyết định 19/2025/QĐ-TTg
   (thường là file Excel) → chuyển sang CSV trên.
2. **Bộ dữ liệu cộng đồng** `ThangLeQuoc/vietnamese-provinces-database` trên GitHub (bản 4.x có 34 tỉnh và 3.321 xã,
   kèm bản dump PostgreSQL). Trước khi dùng: kiểm tra giấy phép của repo, đối chiếu số lượng với nguồn chính thức,
   và viết script chuyển các bảng `provinces`, `wards` của bộ này sang CSV trên.

Ghi nguồn và ngày tải vào báo cáo đồ án.

## Lệnh nạp

```bash
dotnet run --project apps/api/src/TroViec.Migrator -- import-units db/seed/administrative_units/units.csv
```

Lệnh chạy bằng role `app_migrator`, dạng upsert theo `code` (chạy lại được), nạp tỉnh trước rồi tới xã.
Sau khi nạp, kiểm tra:

```sql
SELECT unit_level, count(*) FROM app.administrative_units GROUP BY unit_level;
-- kỳ vọng: province 34, commune 3321 (đối chiếu lại nếu có quyết định điều chỉnh mới)
```

File `units.csv` thật được commit vào repo (dữ liệu công khai); dữ liệu test dùng mã giả trong fixture riêng.

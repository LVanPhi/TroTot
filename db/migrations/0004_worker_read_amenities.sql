-- =============================================================================
-- 0004 — worker đọc tiện nghi để ghép câu tóm tắt của tin khi tính embedding
-- (docs/05-ai-worker.md, mục Embedding: "... tiện nghi: wifi, điều hòa.").
-- Chỉ cấp quyền đọc; worker không ghi hai bảng này.
-- =============================================================================

GRANT SELECT ON app.amenities, app.room_amenities TO app_worker;

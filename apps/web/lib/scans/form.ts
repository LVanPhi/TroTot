/** Quick check form rules, mirroring the API validator (Features/Scans/CreateScan.cs, BR-06). */
import { z } from "zod";

import { parseVndInput } from "@/lib/format";

export const MAX_TEXT = 10_000;
export const MAX_IMAGES = 5;
export const MAX_IMAGE_BYTES = 5 * 1024 * 1024;
export const IMAGE_TYPES = ["image/jpeg", "image/png", "image/webp"] as const;

export const scanSchema = z
  .object({
    text: z.string().max(MAX_TEXT, "Nội dung tối đa 10.000 ký tự"),
    rent: z
      .string()
      .refine((v) => v.trim() === "" || parseVndInput(v) !== null, "Không đọc được số tiền, ví dụ 2.500.000 hoặc 2tr5")
      .refine((v) => {
        const n = parseVndInput(v);
        return n === null || (n >= 100_000 && n <= 100_000_000);
      }, "Giá thuê từ 100.000 đến 100.000.000 đồng"),
    wardCode: z.string(),
    imageCount: z.number().max(MAX_IMAGES, "Tối đa 5 ảnh"),
  })
  .refine((v) => v.text.trim() !== "" || v.imageCount > 0, {
    path: ["text"],
    message: "Dán nội dung hoặc tải ít nhất một ảnh",
  });

export type ScanFormValues = z.infer<typeof scanSchema>;

/** Why a picked file cannot be sent, or null when it is fine. */
export function imageProblem(file: { type: string; size: number }): string | null {
  if (!(IMAGE_TYPES as readonly string[]).includes(file.type)) return "Chỉ nhận ảnh JPEG, PNG, WebP";
  if (file.size > MAX_IMAGE_BYTES) return "Mỗi ảnh tối đa 5 MB";
  return null;
}

/** multipart body for POST /scans. */
export function buildScanForm(values: ScanFormValues, images: File[]): FormData {
  const form = new FormData();
  if (values.text.trim() !== "") form.append("text", values.text);
  const rent = parseVndInput(values.rent);
  if (rent !== null) form.append("claimedRentVnd", String(rent));
  if (values.wardCode) form.append("claimedWardCode", values.wardCode);
  for (const image of images) form.append("images", image);
  return form;
}

/**
 * Text shared from another app (Web Share Target): title, text and url joined as plain text.
 * The url is never fetched (docs/07-security.md, SSRF).
 */
export function sharedText(parts: { title?: string | null; text?: string | null; url?: string | null }): string {
  const seen = new Set<string>();
  return [parts.title, parts.text, parts.url]
    .map((p) => p?.trim() ?? "")
    .filter((p) => p !== "" && !seen.has(p) && (seen.add(p), true))
    .join("\n")
    .slice(0, MAX_TEXT);
}

/**
 * Poster-side listing logic (docs/01-requirements.md, Vòng đời tin đăng; docs/04-api.md, Tin đăng — người đăng).
 * Pure functions so the rules can be tested without the UI.
 */
import { z } from "zod";

import { parseVndInput } from "@/lib/format";

export type ListingStatus =
  | "draft"
  | "screening"
  | "pending_review"
  | "verified"
  | "rejected"
  | "hidden"
  | "removed"
  | "expired";

export const STATUS_LABELS: Record<ListingStatus, string> = {
  draft: "Nháp",
  screening: "Đang sàng lọc",
  pending_review: "Chờ duyệt",
  verified: "Đã xác minh",
  rejected: "Bị từ chối",
  hidden: "Tạm ẩn",
  removed: "Đã gỡ",
  expired: "Hết hạn",
};

export type ListingAction = "edit" | "submit" | "renew" | "unpublish" | "remove" | "appeal";

export const ACTION_LABELS: Record<ListingAction, string> = {
  edit: "Sửa",
  submit: "Gửi duyệt",
  renew: "Gia hạn",
  unpublish: "Chuyển về nháp",
  remove: "Gỡ tin",
  appeal: "Kháng nghị",
};

const DAY = 24 * 60 * 60 * 1000;

/**
 * Actions the API accepts in this state. `hasOpenAppeal` blocks every status change (BR-04); renew needs ≤ 7 days left
 * or expired (BR-03); appeal only right after a rejection or hiding (the API checks the 7-day window).
 */
export function allowedActions(
  status: ListingStatus,
  opts: { expiresAt?: string | null; hasOpenAppeal?: boolean; now?: Date } = {},
): ListingAction[] {
  if (opts.hasOpenAppeal) return [];
  const now = opts.now ?? new Date();
  const soon = opts.expiresAt != null && new Date(opts.expiresAt).getTime() - now.getTime() <= 7 * DAY;
  switch (status) {
    case "draft":
      return ["edit", "submit", "remove"];
    case "verified":
      return soon ? ["renew", "unpublish", "remove"] : ["unpublish", "remove"];
    case "expired":
      return ["renew", "unpublish", "remove"];
    case "rejected":
      return ["appeal", "unpublish", "remove"];
    case "hidden":
      return ["appeal", "remove"];
    case "pending_review":
      return ["remove"];
    default:
      return [];
  }
}

export const PAY_UNITS = { hour: "giờ", shift: "buổi", day: "ngày", month: "tháng" } as const;
export type PayUnit = keyof typeof PAY_UNITS;

const inRange = (v: string, min: number, max: number) => {
  const n = parseVndInput(v);
  return n !== null && n >= min && n <= max;
};

/**
 * One flat form for both kinds (react-hook-form works best with a single shape); the fields of the other kind are
 * ignored. Mirrors the API validator (docs/04-api.md: title 1–150, description 30–5,000, rent 100k–100M…).
 */
export const listingSchema = z
  .object({
    listingType: z.enum(["room", "job"]),
    title: z.string().trim().min(1, "Nhập tiêu đề").max(150, "Tiêu đề tối đa 150 ký tự"),
    description: z.string().trim().min(30, "Mô tả ít nhất 30 ký tự").max(5000, "Mô tả tối đa 5.000 ký tự"),
    wardCode: z.string().regex(/^\d{5}$/, "Chọn phường/xã"),
    streetAddress: z.string().trim().min(1, "Nhập địa chỉ").max(200, "Địa chỉ tối đa 200 ký tự"),
    location: z.object({ lat: z.number().min(8).max(24), lng: z.number().min(102).max(110) }).nullable(),
    rent: z.string(),
    deposit: z.string(),
    areaM2: z.string(),
    isSelfContained: z.boolean(),
    amenityCodes: z.array(z.string()),
    employerName: z.string(),
    pay: z.string(),
    payUnit: z.enum(["hour", "shift", "day", "month"]),
    hoursPerWeek: z.string(),
  })
  .superRefine((v, ctx) => {
    const fail = (path: string, message: string) => ctx.addIssue({ code: "custom", path: [path], message });
    if (v.location === null) fail("location", "Ghim vị trí trên bản đồ");
    if (v.listingType === "room") {
      if (!inRange(v.rent, 100_000, 100_000_000)) fail("rent", "Giá thuê từ 100.000 đến 100.000.000 đồng");
      if (v.deposit.trim() !== "" && (parseVndInput(v.deposit) ?? -1) < 0) fail("deposit", "Tiền cọc không hợp lệ");
      const area = Number(v.areaM2.replace(",", "."));
      if (!Number.isFinite(area) || area < 0.1 || area > 500) fail("areaM2", "Diện tích từ 0,1 đến 500 m²");
    } else {
      if (v.employerName.trim().length < 1 || v.employerName.trim().length > 200) fail("employerName", "Nhập tên nơi làm việc (tối đa 200 ký tự)");
      if (!inRange(v.pay, 1, 100_000_000)) fail("pay", "Mức lương từ 1 đến 100.000.000 đồng");
      const hours = Number(v.hoursPerWeek);
      if (!Number.isInteger(hours) || hours < 1 || hours > 48) fail("hoursPerWeek", "Số giờ mỗi tuần từ 1 đến 48");
    }
  });

export type ListingValues = z.infer<typeof listingSchema>;

export const EMPTY_LISTING: ListingValues = {
  listingType: "room",
  title: "",
  description: "",
  wardCode: "",
  streetAddress: "",
  location: null,
  rent: "",
  deposit: "",
  areaM2: "",
  isSelfContained: false,
  amenityCodes: [],
  employerName: "",
  pay: "",
  payUnit: "hour",
  hoursPerWeek: "",
};

/** Body for POST /listings and PUT /listings/{id} (the caller adds version for PUT). */
export function toRequest(v: ListingValues) {
  const base = {
    listingType: v.listingType,
    title: v.title.trim(),
    description: v.description.trim(),
    wardCode: v.wardCode,
    streetAddress: v.streetAddress.trim(),
    location: v.location!,
  };
  if (v.listingType === "room") {
    return {
      ...base,
      room: {
        monthlyRentVnd: parseVndInput(v.rent)!,
        // Empty = not stated (null); 0 = no deposit (US-301).
        depositVnd: v.deposit.trim() === "" ? null : parseVndInput(v.deposit),
        areaM2: Math.round(Number(v.areaM2.replace(",", ".")) * 10) / 10,
        isSelfContained: v.isSelfContained,
        amenityCodes: v.amenityCodes,
      },
      job: null,
    };
  }
  return {
    ...base,
    room: null,
    job: { employerName: v.employerName.trim(), payVnd: parseVndInput(v.pay)!, payUnit: v.payUnit, hoursPerWeek: Number(v.hoursPerWeek) },
  };
}

/** API field names (validation.failed keys) → form fields. */
export const API_FIELD_MAP: Record<string, keyof ListingValues> = {
  listingType: "listingType",
  title: "title",
  description: "description",
  wardCode: "wardCode",
  streetAddress: "streetAddress",
  location: "location",
  "room.monthlyRentVnd": "rent",
  "room.depositVnd": "deposit",
  "room.areaM2": "areaM2",
  "room.amenityCodes": "amenityCodes",
  "job.employerName": "employerName",
  "job.payVnd": "pay",
  "job.hoursPerWeek": "hoursPerWeek",
};

/** posting.missing codes → what the poster still has to do (BR-02). */
export const MISSING_LABELS: Record<string, string> = {
  phone_verification: "Xác thực số điện thoại",
  email_verification: "Xác thực email trường",
  owner_verification: "Hồ sơ chủ được cán bộ xác minh",
};

export const DOC_TYPES = {
  id_card: "Căn cước công dân",
  business_registration: "Giấy đăng ký kinh doanh",
  property_proof: "Giấy tờ nhà/đất",
  authorization_letter: "Giấy ủy quyền",
  other: "Giấy tờ khác",
} as const;

export const DOC_STATUS = { pending: "Chờ duyệt", accepted: "Đã chấp nhận", rejected: "Bị từ chối" } as const;

export const ownerProfileSchema = z
  .object({
    ownerType: z.enum(["individual", "business"]),
    businessName: z.string().trim().max(200, "Tối đa 200 ký tự"),
    taxCode: z.string().trim(),
  })
  .superRefine((v, ctx) => {
    if (v.ownerType !== "business") return;
    if (!v.businessName) ctx.addIssue({ code: "custom", path: ["businessName"], message: "Doanh nghiệp cần tên" });
    if (!/^\d{10}(-\d{3})?$/.test(v.taxCode))
      ctx.addIssue({ code: "custom", path: ["taxCode"], message: "Mã số thuế gồm 10 số hoặc 10-3 số" });
  });

export type OwnerProfileValues = z.infer<typeof ownerProfileSchema>;

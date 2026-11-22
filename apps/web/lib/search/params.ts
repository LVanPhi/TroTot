/** Search page state ↔ API query, and the "understood conditions" chips (docs/06-frontend.md, /tin). */
import { formatVndShort } from "@/lib/format";

export interface SearchState {
  q: string;
  type: "" | "room" | "job";
  minRent: string;
  maxRent: string;
  selfContained: boolean;
  amenities: string[];
  radiusM: number;
  lat: number | null;
  lng: number | null;
}

export const DEFAULT_STATE: SearchState = {
  q: "",
  type: "",
  minRent: "",
  maxRent: "",
  selfContained: false,
  amenities: [],
  radiusM: 3000,
  lat: null,
  lng: null,
};

const toInt = (v: string) => {
  const n = Number(v.replace(/\D/g, ""));
  return v.trim() === "" || !Number.isFinite(n) ? undefined : n;
};

/** Query for GET /listings or /listings/search; empty values are left out. */
export function toQuery(s: SearchState) {
  return {
    q: s.q.trim() || undefined,
    type: s.type || undefined,
    minRent: toInt(s.minRent),
    maxRent: toInt(s.maxRent),
    selfContained: s.selfContained ? true : undefined,
    amenities: s.amenities.length > 0 ? s.amenities.join(",") : undefined,
    radiusM: s.radiusM,
    lat: s.lat ?? undefined,
    lng: s.lng ?? undefined,
  };
}

export interface Applied {
  type?: string | null;
  minRent?: number | string | null;
  maxRent?: number | string | null;
  amenities?: string[];
  selfContained?: boolean | null;
  nearCampus?: boolean;
}

/** Human-readable chips for appliedFilters, e.g. ["Phòng trọ", "Dưới 2,5 triệu", "Khép kín", "Chỗ để xe"]. */
export function appliedChips(a: Applied, amenityNames: Record<string, string>): string[] {
  const chips: string[] = [];
  if (a.type === "room") chips.push("Phòng trọ");
  if (a.type === "job") chips.push("Việc làm thêm");
  const min = a.minRent == null ? null : Number(a.minRent);
  const max = a.maxRent == null ? null : Number(a.maxRent);
  if (min != null && max != null) chips.push(`${formatVndShort(min)} – ${formatVndShort(max)}`);
  else if (max != null) chips.push(`Dưới ${formatVndShort(max)}`);
  else if (min != null) chips.push(`Từ ${formatVndShort(min)}`);
  if (a.selfContained) chips.push("Khép kín");
  for (const code of a.amenities ?? []) chips.push(amenityNames[code] ?? code);
  if (a.nearCampus) chips.push("Gần trường");
  return chips;
}

export const CAMPUS_STORAGE_KEY = "troviec.campus";

/** Remembered campus code (docs/06-frontend.md, Bản đồ): only the code, nothing personal; storage may be unavailable. */
export function readCampus(): string | null {
  try {
    return window.localStorage.getItem(CAMPUS_STORAGE_KEY);
  } catch {
    return null;
  }
}

export function saveCampus(code: string): void {
  try {
    window.localStorage.setItem(CAMPUS_STORAGE_KEY, code);
  } catch {
    // private mode or blocked storage: the choice just is not remembered
  }
}

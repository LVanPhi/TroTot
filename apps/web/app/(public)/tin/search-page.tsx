"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { List, Map as MapIcon, Search } from "lucide-react";
import dynamic from "next/dynamic";
import { useRouter, useSearchParams } from "next/navigation";
import { useMemo, useState, useSyncExternalStore } from "react";

import { secondaryButton } from "@/components/domain/form-controls";
import { ListingCard, type ListingItem } from "@/components/domain/listing-card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { api } from "@/lib/api/client";
import { appliedChips, DEFAULT_STATE, readCampus, saveCampus, toQuery, type SearchState } from "@/lib/search/params";

const ListingMap = dynamic(() => import("@/components/domain/listing-map").then((m) => m.ListingMap), {
  ssr: false,
  loading: () => <div className="h-[70vh] rounded-xl bg-muted" aria-busy="true" />,
});

const PAGE_SIZE = 20;
const noSubscribe = () => () => {};

/** /tin: natural-language box, filters, list ↔ map, chips of the conditions the API understood (US-401, US-402). */
export function SearchPage() {
  const params = useSearchParams();
  const router = useRouter();
  const [draft, setDraft] = useState<SearchState>({ ...DEFAULT_STATE, q: params.get("q") ?? "" });
  const [state, setState] = useState<SearchState>(draft);
  const [view, setView] = useState<"list" | "map">("list");
  const [page, setPage] = useState(1);
  // The remembered campus lives in localStorage (client only); a pick in this visit overrides it.
  const remembered = useSyncExternalStore(noSubscribe, readCampus, () => null);
  const [picked, setCampus] = useState<string | null>(null);
  const campus = picked ?? remembered;

  const campuses = useQuery({
    queryKey: ["meta", "campuses"],
    staleTime: 60 * 60 * 1000,
    queryFn: async () => (await api.GET("/api/v1/meta/campuses")).data ?? [],
  });
  const amenities = useQuery({
    queryKey: ["meta", "amenities"],
    staleTime: 60 * 60 * 1000,
    queryFn: async () => (await api.GET("/api/v1/meta/amenities")).data ?? [],
  });
  const amenityNames = useMemo(() => Object.fromEntries((amenities.data ?? []).map((a) => [a.code, a.name])), [amenities.data]);

  const chosen = campuses.data?.find((c) => c.code === campus) ?? campuses.data?.[0];
  const center = state.lat != null && state.lng != null ? { lat: state.lat, lng: state.lng } : chosen ? { lat: Number(chosen.lat), lng: Number(chosen.lng) } : null;

  const results = useQuery({
    queryKey: ["listings", state, center, page],
    enabled: center !== null,
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const query = { ...toQuery(state), lat: center!.lat, lng: center!.lng, page, pageSize: PAGE_SIZE };
      if (query.q) {
        const { data } = await api.GET("/api/v1/listings/search", { params: { query: { ...query, q: query.q } } });
        return data ? { items: data.items as ListingItem[], total: Number(data.total), applied: data.appliedFilters, mode: data.mode } : null;
      }
      const { data } = await api.GET("/api/v1/listings", { params: { query } });
      return data ? { items: data.items as ListingItem[], total: Number(data.total), applied: null, mode: "filter" } : null;
    },
  });

  const apply = (next: SearchState) => {
    setState(next);
    setPage(1);
    router.replace(next.q ? `/tin?q=${encodeURIComponent(next.q)}` : "/tin", { scroll: false });
  };

  const chips = results.data?.applied ? appliedChips(results.data.applied, amenityNames) : [];
  return (
    <div className="flex flex-col gap-4">
      <form
        role="search"
        className="flex flex-col gap-3"
        onSubmit={(e) => {
          e.preventDefault();
          apply(draft);
        }}
      >
        <Label htmlFor="q" className="sr-only">
          Tìm bằng câu tự nhiên
        </Label>
        <div className="flex gap-2">
          <Input
            id="q"
            type="search"
            value={draft.q}
            onChange={(e) => setDraft({ ...draft, q: e.target.value })}
            placeholder="Ví dụ: phòng khép kín dưới 2,5 triệu có chỗ để xe"
            className="h-11 text-base"
          />
          <button type="submit" className="inline-flex min-h-11 items-center gap-2 rounded-lg bg-primary px-4 font-medium text-primary-foreground">
            <Search aria-hidden className="size-4" />
            Tìm
          </button>
        </div>
        <details className="rounded-lg border border-border p-3">
          <summary className="min-h-11 cursor-pointer content-center font-medium">Bộ lọc</summary>
          <div className="mt-3 grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="campus">Quanh cơ sở</Label>
              <select
                id="campus"
                className="h-11 rounded-lg border border-input px-3"
                value={chosen?.code ?? ""}
                onChange={(e) => {
                  setCampus(e.target.value);
                  saveCampus(e.target.value);
                  setDraft({ ...draft, lat: null, lng: null });
                }}
              >
                {(campuses.data ?? []).map((c) => (
                  <option key={c.code} value={c.code}>
                    {c.name}
                  </option>
                ))}
              </select>
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="type">Loại tin</Label>
              <select id="type" className="h-11 rounded-lg border border-input px-3" value={draft.type} onChange={(e) => setDraft({ ...draft, type: e.target.value as SearchState["type"] })}>
                <option value="">Tất cả</option>
                <option value="room">Phòng trọ</option>
                <option value="job">Việc làm thêm</option>
              </select>
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="minRent">Giá từ</Label>
              <Input id="minRent" inputMode="numeric" className="h-11" value={draft.minRent} onChange={(e) => setDraft({ ...draft, minRent: e.target.value })} />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="maxRent">Giá đến</Label>
              <Input id="maxRent" inputMode="numeric" className="h-11" value={draft.maxRent} onChange={(e) => setDraft({ ...draft, maxRent: e.target.value })} />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="radius">Bán kính</Label>
              <select id="radius" className="h-11 rounded-lg border border-input px-3" value={draft.radiusM} onChange={(e) => setDraft({ ...draft, radiusM: Number(e.target.value) })}>
                {[1000, 2000, 3000, 5000, 10000].map((m) => (
                  <option key={m} value={m}>
                    {m / 1000} km
                  </option>
                ))}
              </select>
            </div>
            <label className="flex min-h-11 items-center gap-2 self-end">
              <input type="checkbox" className="size-4" checked={draft.selfContained} onChange={(e) => setDraft({ ...draft, selfContained: e.target.checked })} />
              Khép kín
            </label>
            <fieldset className="sm:col-span-2">
              <legend className="mb-1 font-medium">Tiện nghi</legend>
              <div className="grid grid-cols-1 gap-1 sm:grid-cols-3">
                {(amenities.data ?? []).map((a) => (
                  <label key={a.code} className="flex min-h-11 items-center gap-2">
                    <input
                      type="checkbox"
                      className="size-4"
                      checked={draft.amenities.includes(a.code)}
                      onChange={(e) =>
                        setDraft({ ...draft, amenities: e.target.checked ? [...draft.amenities, a.code] : draft.amenities.filter((c) => c !== a.code) })
                      }
                    />
                    {a.name}
                  </label>
                ))}
              </div>
            </fieldset>
          </div>
        </details>
      </form>

      {chips.length > 0 ? (
        <div className="flex flex-wrap items-center gap-2" aria-label="Điều kiện đã hiểu">
          <span className="text-sm text-muted-foreground">Đã hiểu:</span>
          {chips.map((c) => (
            <span key={c} className="rounded-full bg-muted px-3 py-1 text-sm">
              {c}
            </span>
          ))}
        </div>
      ) : null}

      <div className="flex items-center justify-between">
        <p className="text-sm text-muted-foreground" aria-live="polite">
          {results.data ? `${results.data.total} tin` : results.isFetching ? "Đang tìm…" : ""}
        </p>
        <div role="group" aria-label="Kiểu hiển thị" className="flex gap-1">
          <button type="button" aria-pressed={view === "list"} onClick={() => setView("list")} className={`${secondaryButton} gap-2`}>
            <List aria-hidden className="size-4" />
            Danh sách
          </button>
          <button type="button" aria-pressed={view === "map"} onClick={() => setView("map")} className={`${secondaryButton} gap-2`}>
            <MapIcon aria-hidden className="size-4" />
            Bản đồ
          </button>
        </div>
      </div>

      {center === null && campuses.isFetched ? <p>Chưa cấu hình cơ sở đào tạo mặc định; hãy dùng bản đồ để chọn khu vực.</p> : null}
      {view === "map" && center ? (
        <ListingMap items={results.data?.items ?? []} center={center} onSearchHere={(c) => apply({ ...state, lat: c.lat, lng: c.lng })} />
      ) : (
        <ul className="flex flex-col gap-3">
          {(results.data?.items ?? []).map((item, i) => (
            <li key={item.listingId}>
              <ListingCard item={item} eager={i < 2} />
            </li>
          ))}
        </ul>
      )}
      {results.data && results.data.items.length === 0 ? <p>Không có tin nào khớp. Thử nới bộ lọc hoặc tăng bán kính.</p> : null}
      {results.data && results.data.total > PAGE_SIZE ? (
        <nav aria-label="Phân trang" className="flex justify-between">
          <button type="button" className={secondaryButton} disabled={page <= 1} onClick={() => setPage(page - 1)}>
            Trang trước
          </button>
          <button type="button" className={secondaryButton} disabled={page * PAGE_SIZE >= results.data.total} onClick={() => setPage(page + 1)}>
            Trang sau
          </button>
        </nav>
      ) : null}
    </div>
  );
}

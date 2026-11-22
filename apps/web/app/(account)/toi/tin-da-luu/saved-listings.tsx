"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";

import { linkClass, secondaryButton } from "@/components/domain/form-controls";
import { api } from "@/lib/api/client";

/**
 * US-404: expired listings keep their content with a label; listings no longer public for another reason only say
 * "Tin không còn hiển thị" (the API sends no content for them).
 */
export function SavedListings() {
  const queryClient = useQueryClient();
  const saved = useQuery({ queryKey: ["me", "saved"], queryFn: async () => (await api.GET("/api/v1/me/saved-listings")).data ?? [] });

  const unsave = async (listingId: string) => {
    await api.DELETE("/api/v1/me/saved-listings/{listingId}", { params: { path: { listingId } } });
    await queryClient.invalidateQueries({ queryKey: ["me", "saved"] });
  };

  if (saved.isPending) return <p aria-busy="true">Đang tải…</p>;
  if (!saved.data || saved.data.length === 0)
    return (
      <p>
        Chưa lưu tin nào.{" "}
        <Link href="/tin" className={linkClass}>
          Tìm tin
        </Link>
      </p>
    );
  return (
    <ul className="flex flex-col gap-3">
      {saved.data.map((s) => (
        <li key={s.listingId} className="flex flex-wrap items-center justify-between gap-2 rounded-xl border border-border p-3">
          {s.state === "unavailable" ? (
            <span className="text-muted-foreground">Tin không còn hiển thị</span>
          ) : s.state === "expired" ? (
            // Expired listings are no longer public (BR-01): show what was saved, without a link.
            <span className="flex items-center gap-3">
              <span className="font-medium">{s.title}</span>
              <span className="rounded-full bg-muted px-2 py-0.5 text-sm">Đã hết hạn</span>
            </span>
          ) : (
            <Link href={`/tin/${s.listingId}`} className="flex min-h-11 items-center gap-3">
              {s.coverImageUrl ? (
                // eslint-disable-next-line @next/next/no-img-element -- listing photo served by Caddy
                <img src={s.coverImageUrl} alt="" width={64} height={64} loading="lazy" className="size-16 rounded-md object-cover" />
              ) : null}
              <span className="font-medium">{s.title}</span>
            </Link>
          )}
          <button type="button" className={secondaryButton} onClick={() => void unsave(s.listingId)}>
            Bỏ lưu
          </button>
        </li>
      ))}
    </ul>
  );
}

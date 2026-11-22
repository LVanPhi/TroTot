"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Bookmark, BookmarkCheck, Phone } from "lucide-react";
import dynamic from "next/dynamic";
import Link from "next/link";
import { toast } from "sonner";

import { linkClass, secondaryButton } from "@/components/domain/form-controls";
import { ReportButton } from "@/components/domain/report-scan-button";
import { api } from "@/lib/api/client";
import { messageFor } from "@/lib/api/errors";
import { toProblem } from "@/lib/api/problem";
import { can, useMe, useSession } from "@/lib/auth/use-auth";

const ListingLocation = dynamic(() => import("./listing-location").then((m) => m.ListingLocation), {
  ssr: false,
  loading: () => <div className="h-64 rounded-xl bg-muted" aria-busy="true" />,
});

/** Signed-in parts of the detail page: contact phone (BR-14), save (US-404), report (US-406). */
export function ListingActions({ listingId, location }: { listingId: string; location: { lat: number; lng: number } }) {
  const { status } = useSession();
  const { data: me } = useMe();
  const queryClient = useQueryClient();
  const personal = useQuery({
    queryKey: ["listing", listingId, "personal"],
    enabled: status === "signed_in",
    queryFn: async () => (await api.GET("/api/v1/listings/{listingId}", { params: { path: { listingId } } })).data ?? null,
  });

  const toggleSave = async () => {
    const saved = personal.data?.isSaved === true;
    const result = saved
      ? await api.DELETE("/api/v1/me/saved-listings/{listingId}", { params: { path: { listingId } } })
      : await api.PUT("/api/v1/me/saved-listings/{listingId}", { params: { path: { listingId } } });
    if (!result.response.ok) return toast.error(messageFor(toProblem((result as { error?: unknown }).error).code));
    toast.success(saved ? "Đã bỏ lưu." : "Đã lưu tin.");
    await queryClient.invalidateQueries({ queryKey: ["listing", listingId, "personal"] });
  };

  return (
    <section className="flex flex-col gap-4">
      <h2 className="text-lg font-semibold">Vị trí và liên hệ</h2>
      <ListingLocation location={location} />
      {status === "signed_in" ? (
        <div className="flex flex-wrap gap-2">
          {personal.data?.contactPhone ? (
            <a href={`tel:${personal.data.contactPhone}`} className={`${secondaryButton} gap-2`}>
              <Phone aria-hidden className="size-4" />
              {personal.data.contactPhone}
            </a>
          ) : null}
          {can(me, "listing.save") ? (
            <button type="button" onClick={() => void toggleSave()} className={`${secondaryButton} gap-2`} aria-pressed={personal.data?.isSaved === true}>
              {personal.data?.isSaved ? <BookmarkCheck aria-hidden className="size-4" /> : <Bookmark aria-hidden className="size-4" />}
              {personal.data?.isSaved ? "Đã lưu" : "Lưu tin"}
            </button>
          ) : null}
          {can(me, "report.create") ? <ReportButton target={{ listingId }} /> : null}
        </div>
      ) : status === "guest" ? (
        <p>
          <Link href={`/dang-nhap?next=/tin/${listingId}`} className={linkClass}>
            Đăng nhập
          </Link>{" "}
          để xem số điện thoại người đăng, lưu tin hoặc báo cáo.
        </p>
      ) : null}
      <p className="text-sm text-muted-foreground">
        Luôn xem phòng hoặc gặp nhà tuyển dụng trực tiếp trước khi chuyển tiền. Bạn có thể{" "}
        <Link href="/kiem-tra" className={linkClass}>
          kiểm tra một tin
        </Link>{" "}
        bất kỳ trước khi quyết định.
      </p>
    </section>
  );
}

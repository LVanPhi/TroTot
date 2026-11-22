"use client";

import { useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { toast } from "sonner";

import { linkClass, secondaryButton } from "@/components/domain/form-controls";
import { ImageUploader, type ListingImage } from "@/components/domain/image-uploader";
import { ListingForm } from "@/components/domain/listing-form";
import { VersionConflictDialog } from "@/components/domain/version-conflict-dialog";
import { api } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import { toRequest } from "@/lib/listings/model";
import { listingKey, statusOf, toFormValues, useMyListing } from "@/lib/listings/use-listing";

/** Drafts only (US-307): content form with the version, then photos (US-303). */
export function EditListing({ id }: { id: string }) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { data, refetch } = useMyListing(id);
  const [images, setImages] = useState<ListingImage[] | null>(null);
  const [conflict, setConflict] = useState(false);

  if (!data) return <p aria-busy="true">Đang tải…</p>;
  if (statusOf(data) !== "draft")
    return (
      <p>
        Chỉ sửa được tin ở trạng thái Nháp.{" "}
        <Link href={`/toi/tin-cua-toi/${id}`} className={linkClass}>
          Chuyển tin về nháp
        </Link>{" "}
        trước khi sửa.
      </p>
    );

  const shown = images ?? data.images.map((i) => ({ imageId: Number(i.imageId), url: i.url }));
  return (
    <div className="flex flex-col gap-8">
      <section className="flex flex-col gap-3">
        <h2 className="text-lg font-semibold">Ảnh</h2>
        <ImageUploader listingId={id} images={shown} onChange={setImages} />
      </section>
      <section className="flex flex-col gap-3">
        <h2 className="text-lg font-semibold">Nội dung</h2>
        <ListingForm
          key={String(data.version)}
          initial={toFormValues(data)}
          initialCommune={{ code: data.wardCode, name: data.wardName, provinceCode: "", provinceName: "" }}
          typeLocked
          submitLabel="Lưu thay đổi"
          submit={async (values) => {
            const { error } = await api.PUT("/api/v1/listings/{listingId}", {
              params: { path: { listingId: id } },
              body: { ...toRequest(values), version: Number(data.version) },
            });
            if (error) {
              const p = toProblem(error);
              if (p.code === "listing.version_conflict") {
                setConflict(true);
                return null;
              }
              return p;
            }
            toast.success("Đã lưu.");
            await queryClient.invalidateQueries({ queryKey: listingKey(id) });
            return null;
          }}
        />
      </section>
      <button type="button" className={secondaryButton} onClick={() => router.push(`/toi/tin-cua-toi/${id}`)}>
        Xong, tới trang gửi duyệt
      </button>
      <VersionConflictDialog
        open={conflict}
        onReload={() => {
          setConflict(false);
          void refetch();
        }}
      />
    </div>
  );
}

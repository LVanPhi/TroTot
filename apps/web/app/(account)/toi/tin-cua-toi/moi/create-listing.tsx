"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";

import { ListingForm } from "@/components/domain/listing-form";
import { api } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import { EMPTY_LISTING, toRequest } from "@/lib/listings/model";

/** POST /listings, then straight to the edit page to add photos (US-303). */
export function CreateListing() {
  const router = useRouter();
  const queryClient = useQueryClient();
  return (
    <ListingForm
      initial={EMPTY_LISTING}
      initialCommune={null}
      typeLocked={false}
      submitLabel="Lưu nháp"
      submit={async (values) => {
        const { data, error } = await api.POST("/api/v1/listings", { body: toRequest(values) });
        if (error || !data) return toProblem(error);
        await queryClient.invalidateQueries({ queryKey: ["me", "listings"] });
        router.push(`/toi/tin-cua-toi/${data.listingId}/sua?moi=1`);
        return null;
      }}
    />
  );
}

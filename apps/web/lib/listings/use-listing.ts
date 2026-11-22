"use client";

import { useQuery } from "@tanstack/react-query";

import { api, type Schemas } from "@/lib/api/client";
import { toProblem, type Problem } from "@/lib/api/problem";

import type { ListingStatus, ListingValues } from "./model";

export type ListingDetail = Schemas["ListingReadEndpointsDetail"];

export const listingKey = (id: string) => ["listing", id] as const;

export class ListingLoadError extends Error {
  constructor(readonly problem: Problem) {
    super(problem.code);
  }
}

/** GET /listings/{id} as the poster (status and version included). Polls while the listing is being screened. */
export function useMyListing(id: string) {
  return useQuery({
    queryKey: listingKey(id),
    queryFn: async (): Promise<ListingDetail> => {
      const result = await api.GET("/api/v1/listings/{listingId}", { params: { path: { listingId: id } } });
      if (!result.response.ok || !result.data) throw new ListingLoadError({ ...toProblem(result.error), status: result.response.status });
      return result.data;
    },
    retry: (count, error) => !(error instanceof ListingLoadError) && count < 2,
    refetchInterval: (q) => (q.state.data?.status === "screening" ? 5000 : false),
  });
}

export function useHistory(id: string, status: string | null | undefined) {
  return useQuery({
    queryKey: ["listing", id, "history", status],
    queryFn: async () => (await api.GET("/api/v1/listings/{listingId}/history", { params: { path: { listingId: id } } })).data ?? [],
  });
}

/** Open appeal for this listing, from /me/appeals (posters only see their own). */
export function useOpenAppeal(id: string, enabled: boolean) {
  return useQuery({
    queryKey: ["me", "appeals"],
    enabled,
    queryFn: async () => (await api.GET("/api/v1/me/appeals")).data ?? [],
    select: (appeals) => appeals.filter((a) => a.listingId === id),
  });
}

const money = (n: number | string | null | undefined) => (n == null ? "" : new Intl.NumberFormat("vi-VN").format(Number(n)));

/** Detail → form values for editing (US-307). */
export function toFormValues(d: ListingDetail): ListingValues {
  return {
    listingType: d.listingType === "job" ? "job" : "room",
    title: d.title,
    description: d.description,
    wardCode: d.wardCode,
    streetAddress: d.streetAddress,
    location: { lat: Number(d.location.lat), lng: Number(d.location.lng) },
    rent: money(d.room?.monthlyRentVnd),
    deposit: money(d.room?.depositVnd),
    areaM2: d.room ? String(d.room.areaM2).replace(".", ",") : "",
    isSelfContained: d.room?.isSelfContained ?? false,
    amenityCodes: d.amenities.map((a) => a.code),
    employerName: d.job?.employerName ?? "",
    pay: money(d.job?.payVnd),
    payUnit: (d.job?.payUnit as ListingValues["payUnit"]) ?? "hour",
    hoursPerWeek: d.job ? String(d.job.hoursPerWeek) : "",
  };
}

export const statusOf = (d: ListingDetail | undefined) => (d?.status ?? "draft") as ListingStatus;

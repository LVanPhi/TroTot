"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery } from "@tanstack/react-query";
import dynamic from "next/dynamic";
import { useState } from "react";
import { Controller, useForm, useWatch } from "react-hook-form";

import { CommuneCombobox, type Commune } from "@/components/domain/commune-combobox";
import { FormError, SubmitButton, TextField } from "@/components/domain/form-controls";
import { Label } from "@/components/ui/label";
import { api } from "@/lib/api/client";
import { messageFor } from "@/lib/api/errors";
import type { Problem } from "@/lib/api/problem";
import { API_FIELD_MAP, listingSchema, PAY_UNITS, type ListingValues } from "@/lib/listings/model";

// maplibre touches window: load the map on the client only.
const LocationPicker = dynamic(() => import("./location-picker").then((m) => m.LocationPicker), {
  ssr: false,
  loading: () => <div className="h-72 rounded-lg bg-muted" aria-busy="true" />,
});

const textarea = "min-h-36 rounded-lg border border-input p-3 text-base";

/**
 * Room / job form (US-301, US-302, US-307). `submit` returns the API problem (or null on success) so create and edit
 * share field mapping: validation.failed errors land on their fields.
 */
export function ListingForm({
  initial,
  initialCommune,
  typeLocked,
  submitLabel,
  submit,
}: {
  initial: ListingValues;
  initialCommune: Commune | null;
  typeLocked: boolean;
  submitLabel: string;
  submit: (values: ListingValues) => Promise<Problem | null>;
}) {
  const [commune, setCommune] = useState<Commune | null>(initialCommune);
  const form = useForm<ListingValues>({ resolver: zodResolver(listingSchema), defaultValues: initial });
  const { errors, isSubmitting } = form.formState;
  const type = useWatch({ control: form.control, name: "listingType" });

  const { data: amenities = [] } = useQuery({
    queryKey: ["meta", "amenities"],
    staleTime: 60 * 60 * 1000,
    queryFn: async () => (await api.GET("/api/v1/meta/amenities")).data ?? [],
  });

  const onSubmit = form.handleSubmit(async (values) => {
    const problem = await submit(values);
    if (!problem) return;
    let placed = false;
    if (problem.code === "validation.failed" && problem.errors) {
      for (const [field, texts] of Object.entries(problem.errors)) {
        const target = API_FIELD_MAP[field];
        if (target) {
          form.setError(target, { type: "server", message: texts[0] });
          placed = true;
        }
      }
    }
    if (!placed) form.setError("root.server", { type: "server", message: messageFor(problem.code) });
  });

  return (
    <form onSubmit={onSubmit} noValidate className="flex flex-col gap-5">
      <fieldset className="flex flex-col gap-2" disabled={typeLocked}>
        <legend className="mb-1 font-medium">Loại tin</legend>
        <div className="flex gap-4">
          {(["room", "job"] as const).map((t) => (
            <label key={t} className="flex min-h-11 items-center gap-2">
              <input type="radio" value={t} {...form.register("listingType")} className="size-4" />
              {t === "room" ? "Phòng trọ" : "Việc làm thêm"}
            </label>
          ))}
        </div>
      </fieldset>

      <TextField id="title" label="Tiêu đề" error={errors.title?.message} {...form.register("title")} />
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="description">Mô tả</Label>
        <textarea id="description" className={textarea} aria-invalid={errors.description ? true : undefined} {...form.register("description")} />
        {errors.description ? <p role="alert" className="text-sm text-destructive">{errors.description.message}</p> : null}
      </div>

      <Controller
        control={form.control}
        name="wardCode"
        render={({ field }) => (
          <CommuneCombobox
            label="Phường/xã"
            value={commune}
            error={errors.wardCode?.message}
            onChange={(c) => {
              setCommune(c);
              field.onChange(c?.code ?? "");
            }}
          />
        )}
      />
      <TextField id="streetAddress" label="Địa chỉ (số nhà, ngõ, đường)" error={errors.streetAddress?.message} {...form.register("streetAddress")} />
      <Controller
        control={form.control}
        name="location"
        render={({ field }) => (
          <div className="flex flex-col gap-1.5">
            <span className="font-medium">{type === "job" ? "Địa điểm làm việc thật" : "Vị trí phòng"}</span>
            <LocationPicker value={field.value} onChange={field.onChange} error={errors.location?.message} />
          </div>
        )}
      />

      {type === "room" ? (
        <fieldset className="flex flex-col gap-4">
          <legend className="sr-only">Thông tin phòng</legend>
          <TextField id="rent" label="Giá thuê mỗi tháng" inputMode="numeric" placeholder="2.500.000 hoặc 2tr5" error={errors.rent?.message} {...form.register("rent")} />
          <TextField id="deposit" label="Tiền cọc" hint="Để trống nếu chưa nêu; nhập 0 nếu không cần cọc." inputMode="numeric" error={errors.deposit?.message} {...form.register("deposit")} />
          <TextField id="areaM2" label="Diện tích (m²)" inputMode="decimal" error={errors.areaM2?.message} {...form.register("areaM2")} />
          <label className="flex min-h-11 items-center gap-2">
            <input type="checkbox" className="size-4" {...form.register("isSelfContained")} />
            Phòng khép kín (vệ sinh riêng)
          </label>
          <fieldset className="flex flex-col gap-2">
            <legend className="mb-1 font-medium">Tiện nghi</legend>
            <div className="grid grid-cols-1 gap-1 sm:grid-cols-2">
              {amenities.map((a) => (
                <label key={a.code} className="flex min-h-11 items-center gap-2">
                  <input type="checkbox" value={a.code} className="size-4" {...form.register("amenityCodes")} />
                  {a.name}
                </label>
              ))}
            </div>
            {errors.amenityCodes ? <p role="alert" className="text-sm text-destructive">{errors.amenityCodes.message}</p> : null}
          </fieldset>
        </fieldset>
      ) : (
        <fieldset className="flex flex-col gap-4">
          <legend className="sr-only">Thông tin việc làm</legend>
          <TextField id="employerName" label="Tên nơi làm việc" error={errors.employerName?.message} {...form.register("employerName")} />
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <TextField id="pay" label="Mức lương" inputMode="numeric" error={errors.pay?.message} {...form.register("pay")} />
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="payUnit">Tính theo</Label>
              <select id="payUnit" className="h-11 rounded-lg border border-input px-3" {...form.register("payUnit")}>
                {Object.entries(PAY_UNITS).map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </select>
            </div>
          </div>
          <TextField id="hoursPerWeek" label="Số giờ mỗi tuần" inputMode="numeric" error={errors.hoursPerWeek?.message} {...form.register("hoursPerWeek")} />
        </fieldset>
      )}

      <FormError message={errors.root?.server?.message} />
      <SubmitButton pending={isSubmitting}>{submitLabel}</SubmitButton>
    </form>
  );
}

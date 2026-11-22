import { BadgeCheck, Briefcase, CalendarDays, MapPin, Ruler } from "lucide-react";
import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { connection } from "next/server";
import { Suspense } from "react";

import { serverApi } from "@/lib/api/server";
import { formatDateTime, formatPay, formatVnd, type PayUnit } from "@/lib/format";

import { ListingActions } from "./listing-actions";
import { Reviews } from "./reviews";

type Params = PageProps<"/tin/[listingId]">["params"];

/** Anonymous SSR fetch: hidden / removed / draft listings answer 404 (BR-01). */
async function load(listingId: string) {
  await connection(); // request-time data, never prerendered
  if (!/^[0-9a-f-]{36}$/i.test(listingId)) return null;
  const { data } = await serverApi.GET("/api/v1/listings/{listingId}", { params: { path: { listingId } } });
  return data ?? null;
}

export async function generateMetadata({ params }: { params: Params }): Promise<Metadata> {
  const listing = await load((await params).listingId);
  if (!listing) return { title: "Không tìm thấy tin" };
  const description = listing.description.slice(0, 160);
  return {
    title: listing.title,
    description,
    openGraph: { title: listing.title, description, images: listing.images[0] ? [{ url: listing.images[0].url }] : [] },
  };
}

async function Detail({ params }: { params: Params }) {
  const { listingId } = await params;
  const l = await load(listingId);
  if (!l) notFound();

  return (
    <article className="flex flex-col gap-5">
      {l.images.length > 0 ? (
        <div className="flex snap-x gap-2 overflow-x-auto">
          {l.images.map((img, i) => (
            // eslint-disable-next-line @next/next/no-img-element -- photos come from Caddy, already resized WebP
            <img
              key={String(img.imageId)}
              src={img.url}
              alt={`Ảnh ${i + 1} của tin`}
              width={Number(img.width)}
              height={Number(img.height)}
              loading={i === 0 ? "eager" : "lazy"}
              className="h-64 w-auto snap-start rounded-xl object-cover"
            />
          ))}
        </div>
      ) : null}

      <div className="flex flex-col gap-2">
        <h1 className="text-2xl font-semibold tracking-tight">{l.title}</h1>
        {l.expiresAt ? (
          <p className="inline-flex items-center gap-1 text-emerald-800">
            <BadgeCheck aria-hidden className="size-5" />
            Đã xác minh đến {formatDateTime(l.expiresAt).slice(0, 10)}
          </p>
        ) : null}
        {l.room ? (
          <p className="text-xl font-semibold">
            {formatVnd(Number(l.room.monthlyRentVnd))}/tháng
            {l.room.depositVnd != null ? (
              <span className="text-base font-normal text-muted-foreground">
                {" "}
                · {Number(l.room.depositVnd) === 0 ? "Không cần cọc" : `Cọc ${formatVnd(Number(l.room.depositVnd))}`}
              </span>
            ) : null}
          </p>
        ) : null}
        {l.job ? (
          <p className="text-xl font-semibold">
            {formatPay(Number(l.job.payVnd), l.job.payUnit as PayUnit)} · {Number(l.job.hoursPerWeek)} giờ/tuần
          </p>
        ) : null}
      </div>

      <ul className="flex flex-col gap-2 text-sm">
        <li className="flex items-start gap-2">
          <MapPin aria-hidden className="mt-0.5 size-4 shrink-0" />
          {l.streetAddress}, {l.wardName}
        </li>
        {l.room ? (
          <li className="flex items-start gap-2">
            <Ruler aria-hidden className="mt-0.5 size-4 shrink-0" />
            {String(l.room.areaM2).replace(".", ",")} m²{l.room.isSelfContained ? " · Khép kín" : ""}
          </li>
        ) : null}
        {l.job ? (
          <li className="flex items-start gap-2">
            <Briefcase aria-hidden className="mt-0.5 size-4 shrink-0" />
            {l.job.employerName}
          </li>
        ) : null}
        <li className="flex items-start gap-2">
          <CalendarDays aria-hidden className="mt-0.5 size-4 shrink-0" />
          Người đăng: {l.poster.displayName}
          {l.poster.verifiedOwner ? " · hồ sơ đã xác minh" : ""} · tham gia {formatDateTime(l.poster.memberSince).slice(3, 10)}
        </li>
      </ul>

      {l.amenities.length > 0 ? (
        <section className="flex flex-col gap-2">
          <h2 className="text-lg font-semibold">Tiện nghi</h2>
          <ul className="flex flex-wrap gap-2">
            {l.amenities.map((a) => (
              <li key={a.code} className="rounded-full bg-muted px-3 py-1 text-sm">
                {a.name}
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      <section className="flex flex-col gap-2">
        <h2 className="text-lg font-semibold">Mô tả</h2>
        <p className="whitespace-pre-line">{l.description}</p>
      </section>

      <ListingActions listingId={listingId} location={{ lat: Number(l.location.lat), lng: Number(l.location.lng) }} />
      <Reviews listingId={listingId} average={l.reviewSummary.average == null ? null : Number(l.reviewSummary.average)} count={Number(l.reviewSummary.count)} />
    </article>
  );
}

export default function ListingPage({ params }: PageProps<"/tin/[listingId]">) {
  return (
    <main className="mx-auto flex w-full max-w-3xl flex-1 flex-col gap-4 px-4 py-6">
      <Suspense fallback={<p aria-busy="true">Đang tải…</p>}>
        <Detail params={params} />
      </Suspense>
    </main>
  );
}

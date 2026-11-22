import { Search, ShieldCheck } from "lucide-react";
import Link from "next/link";
import { connection } from "next/server";
import { Suspense } from "react";

import { ListingCard, type ListingItem } from "@/components/domain/listing-card";
import { serverApi } from "@/lib/api/server";

const linkBase =
  "inline-flex min-h-11 items-center justify-center gap-2 rounded-lg px-4 font-medium focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring";

/** Newest verified listings around the first campus (docs/06-frontend.md, /). */
async function Latest() {
  await connection(); // request-time data, never prerendered
  const campuses = (await serverApi.GET("/api/v1/meta/campuses")).data ?? [];
  const first = campuses[0];
  if (!first) return null;
  const { data } = await serverApi.GET("/api/v1/listings", {
    params: { query: { lat: first.lat, lng: first.lng, radiusM: 5000, sort: "newest", pageSize: 6 } },
  });
  if (!data || data.items.length === 0) return <p className="text-muted-foreground">Chưa có tin nào đã xác minh quanh {first.name}.</p>;
  return (
    <ul className="flex flex-col gap-3">
      {data.items.map((item, i) => (
        <li key={item.listingId}>
          <ListingCard item={item as ListingItem} eager={i < 2} />
        </li>
      ))}
    </ul>
  );
}

export default function HomePage() {
  return (
    <main className="mx-auto flex w-full max-w-3xl flex-1 flex-col gap-8 px-4 py-10">
      <div className="flex flex-col gap-4">
        <h1 className="text-3xl font-bold tracking-tight">Trọ &amp; Việc Sạch</h1>
        <p className="text-lg text-muted-foreground">
          Phòng trọ và việc làm thêm đã được xác minh. Thấy tin trên mạng? Dán vào để xem các dấu hiệu rủi ro trước khi chuyển tiền.
        </p>
        <Link href="/kiem-tra" className={`${linkBase} bg-primary py-3 text-lg text-primary-foreground hover:bg-primary/85`}>
          <ShieldCheck aria-hidden className="size-6" />
          Kiểm tra một tin
        </Link>
        <form action="/tin" role="search" className="flex gap-2">
          <label htmlFor="home-q" className="sr-only">
            Tìm bằng câu tự nhiên
          </label>
          <input
            id="home-q"
            name="q"
            type="search"
            placeholder="Phòng khép kín dưới 2,5 triệu gần trường"
            className="h-11 min-w-0 flex-1 rounded-lg border border-input px-3 text-base"
          />
          <button type="submit" className={`${linkBase} border border-border hover:bg-muted`}>
            <Search aria-hidden className="size-5" />
            Tìm
          </button>
        </form>
      </div>
      <section className="flex flex-col gap-3">
        <h2 className="text-xl font-semibold">Tin mới đã xác minh</h2>
        <Suspense fallback={<p aria-busy="true">Đang tải…</p>}>
          <Latest />
        </Suspense>
      </section>
    </main>
  );
}

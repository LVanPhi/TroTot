import Link from "next/link";
import { Search, ShieldCheck } from "lucide-react";

const linkBase =
  "inline-flex min-h-11 items-center justify-center gap-2 rounded-lg px-4 font-medium focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring";

/** Temporary home page (T0.6). Real sections arrive with the listing and scan features. */
export default function HomePage() {
  return (
    <main className="mx-auto flex w-full max-w-2xl flex-1 flex-col justify-center gap-6 px-4 py-12">
      <h1 className="text-3xl font-bold tracking-tight">Trọ &amp; Việc Sạch</h1>
      <p className="text-lg text-muted-foreground">
        Tìm phòng trọ và việc làm thêm đã được xác minh. Dán một tin bạn thấy trên mạng để xem các dấu hiệu
        rủi ro trước khi chuyển tiền.
      </p>
      <div className="flex flex-col gap-3 sm:flex-row">
        <Link href="/kiem-tra" className={`${linkBase} bg-primary text-primary-foreground hover:bg-primary/85`}>
          <ShieldCheck aria-hidden className="size-5" />
          Kiểm tra tin
        </Link>
        <Link href="/tin" className={`${linkBase} border border-border hover:bg-muted`}>
          <Search aria-hidden className="size-5" />
          Tìm tin
        </Link>
      </div>
    </main>
  );
}

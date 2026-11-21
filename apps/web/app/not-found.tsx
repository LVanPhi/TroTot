import Link from "next/link";

export default function NotFound() {
  return (
    <main className="mx-auto flex w-full max-w-md flex-1 flex-col items-center justify-center gap-4 px-4 py-12 text-center">
      <p className="text-sm font-medium text-muted-foreground">404</p>
      <h1 className="text-2xl font-semibold">Không tìm thấy trang</h1>
      <p className="text-muted-foreground">Trang bạn tìm không tồn tại hoặc không còn hiển thị.</p>
      <Link
        href="/"
        className="inline-flex min-h-11 items-center justify-center rounded-lg border border-border px-4 font-medium hover:bg-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
      >
        Về trang chủ
      </Link>
    </main>
  );
}

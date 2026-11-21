import type { Metadata } from "next";
import { WifiOff } from "lucide-react";

import { RetryButton } from "./retry-button";

export const metadata: Metadata = { title: "Ngoại tuyến" };

/** Fallback page the service worker serves when a navigation fails offline (docs/06-frontend.md, PWA). */
export default function OfflinePage() {
  return (
    <main className="mx-auto flex w-full max-w-md flex-1 flex-col items-center justify-center gap-4 px-4 py-12 text-center">
      <WifiOff aria-hidden className="size-10 text-muted-foreground" />
      <h1 className="text-2xl font-semibold">Bạn đang ngoại tuyến</h1>
      <p className="text-muted-foreground">Kiểm tra kết nối mạng rồi thử lại.</p>
      <RetryButton />
    </main>
  );
}

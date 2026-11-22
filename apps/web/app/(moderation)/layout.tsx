import { Suspense, type ReactNode } from "react";

import { ModerationNav } from "@/components/domain/moderation-nav";

/** Shared frame of the moderation pages; each page checks its own permission. */
export default function ModerationLayout({ children }: { children: ReactNode }) {
  return (
    <div className="mx-auto flex w-full max-w-6xl flex-1 flex-col gap-4 px-4 py-6">
      <Suspense fallback={null}>
        <ModerationNav />
      </Suspense>
      {children}
    </div>
  );
}

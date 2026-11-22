import { Suspense, type ReactNode } from "react";

import { AdminNav } from "./admin-nav";

/** Admin pages (US-801…805); each page checks its own permission. */
export default function AdminLayout({ children }: { children: ReactNode }) {
  return (
    <div className="mx-auto flex w-full max-w-6xl flex-1 flex-col gap-4 px-4 py-6">
      <Suspense fallback={null}>
        <AdminNav />
      </Suspense>
      {children}
    </div>
  );
}

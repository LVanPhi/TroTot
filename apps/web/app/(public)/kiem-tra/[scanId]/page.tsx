import type { Metadata } from "next";
import { Suspense } from "react";

import { ScanPending } from "@/components/domain/scan-result";

import { ScanStatus } from "./scan-status";

// Results are private to whoever has the link: keep them out of search engines.
export const metadata: Metadata = { title: "Kết quả kiểm tra", robots: { index: false, follow: false } };

async function ScanStatusForParams({ params }: { params: PageProps<"/kiem-tra/[scanId]">["params"] }) {
  const { scanId } = await params;
  return <ScanStatus scanId={scanId} />;
}

export default function ScanResultPage({ params }: PageProps<"/kiem-tra/[scanId]">) {
  return (
    <main className="mx-auto flex w-full max-w-2xl flex-1 flex-col gap-6 px-4 py-8">
      <h1 className="text-2xl font-semibold tracking-tight">Kết quả kiểm tra</h1>
      <Suspense fallback={<ScanPending />}>
        <ScanStatusForParams params={params} />
      </Suspense>
    </main>
  );
}

import type { Metadata } from "next";

import { PermissionGate } from "@/components/domain/permission-gate";

import { SavedListings } from "./saved-listings";

export const metadata: Metadata = { title: "Tin đã lưu" };

export default function SavedListingsPage() {
  return (
    <PermissionGate permission="listing.save">
      <main className="mx-auto flex w-full max-w-3xl flex-1 flex-col gap-4 px-4 py-8">
        <h1 className="text-2xl font-semibold tracking-tight">Tin đã lưu</h1>
        <SavedListings />
      </main>
    </PermissionGate>
  );
}

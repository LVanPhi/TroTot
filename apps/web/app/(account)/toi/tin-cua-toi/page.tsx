import type { Metadata } from "next";

import { PermissionGate } from "@/components/domain/permission-gate";

import { MyListings } from "./my-listings";

export const metadata: Metadata = { title: "Tin của tôi" };

export default function MyListingsPage() {
  return (
    <PermissionGate permission="listing.create">
      <main className="mx-auto flex w-full max-w-3xl flex-1 flex-col gap-4 px-4 py-8">
        <MyListings />
      </main>
    </PermissionGate>
  );
}

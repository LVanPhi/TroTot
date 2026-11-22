"use client";

import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";

import { secondaryButton } from "./form-controls";

/** 409 listing.version_conflict (docs/06-frontend.md, Gọi API): reload to get the latest version. */
export function VersionConflictDialog({ open, onReload }: { open: boolean; onReload: () => void }) {
  return (
    <Dialog open={open}>
      <DialogContent showCloseButton={false}>
        <DialogHeader>
          <DialogTitle>Tin vừa được cập nhật ở nơi khác</DialogTitle>
          <DialogDescription>Tải lại để xem phiên bản mới nhất rồi thao tác lại.</DialogDescription>
        </DialogHeader>
        <button type="button" className={secondaryButton} onClick={onReload}>
          Tải lại
        </button>
      </DialogContent>
    </Dialog>
  );
}

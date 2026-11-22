"use client";

import { closestCenter, DndContext, KeyboardSensor, PointerSensor, useSensor, useSensors, type DragEndEvent } from "@dnd-kit/core";
import { arrayMove, rectSortingStrategy, SortableContext, sortableKeyboardCoordinates, useSortable } from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { GripVertical, ImagePlus, X } from "lucide-react";
import { useState } from "react";

import { api } from "@/lib/api/client";
import { messageFor } from "@/lib/api/errors";
import { toProblem } from "@/lib/api/problem";
import { imageProblem } from "@/lib/scans/form";

export interface ListingImage {
  imageId: number;
  url: string;
}

export const MAX_LISTING_IMAGES = 10;

function SortableImage({ image, index, onRemove }: { image: ListingImage; index: number; onRemove: () => void }) {
  const { attributes, listeners, setNodeRef, transform, transition } = useSortable({ id: image.imageId });
  return (
    <li ref={setNodeRef} style={{ transform: CSS.Transform.toString(transform), transition }} className="relative">
      {/* eslint-disable-next-line @next/next/no-img-element -- listing photo served by Caddy */}
      <img src={image.url} alt={`Ảnh số ${index + 1}`} width={112} height={112} className="size-28 rounded-lg object-cover" />
      <button
        type="button"
        {...attributes}
        {...listeners}
        aria-label={`Kéo để đổi vị trí ảnh số ${index + 1}`}
        className="absolute bottom-1 left-1 inline-flex size-9 cursor-grab items-center justify-center rounded-md bg-background/90"
      >
        <GripVertical aria-hidden className="size-4" />
      </button>
      <button
        type="button"
        onClick={onRemove}
        aria-label={`Xóa ảnh số ${index + 1}`}
        className="absolute -top-2 -right-2 inline-flex size-9 items-center justify-center rounded-full border border-border bg-background"
      >
        <X aria-hidden className="size-4" />
      </button>
    </li>
  );
}

/**
 * Listing photos (docs/06-frontend.md, Form): checked before upload, sent one by one with progress, one failure does
 * not stop the others, drag (or keyboard) to reorder. Drafts only — the API refuses otherwise.
 */
export function ImageUploader({ listingId, images, onChange }: { listingId: string; images: ListingImage[]; onChange: (v: ListingImage[]) => void }) {
  const [errors, setErrors] = useState<string[]>([]);
  const [progress, setProgress] = useState<string | null>(null);
  const sensors = useSensors(useSensor(PointerSensor), useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }));

  const upload = async (files: FileList | null) => {
    if (!files) return;
    const picked = Array.from(files).slice(0, MAX_LISTING_IMAGES - images.length);
    const problems: string[] = [];
    let current = images;
    for (const [i, file] of picked.entries()) {
      const bad = imageProblem(file);
      if (bad) {
        problems.push(`${file.name}: ${bad}`);
        continue;
      }
      setProgress(`Đang tải ảnh ${i + 1}/${picked.length}…`);
      const form = new FormData();
      form.append("file", file);
      const { data, error } = await api.POST("/api/v1/listings/{listingId}/images", {
        params: { path: { listingId } },
        body: form as unknown as string,
        bodySerializer: () => form,
      });
      if (error || !data) problems.push(`${file.name}: ${messageFor(toProblem(error).code)}`);
      else {
        current = [...current, { imageId: Number(data.imageId), url: data.url }];
        onChange(current);
      }
    }
    if (Array.from(files).length > picked.length) problems.push("Tối đa 10 ảnh mỗi tin");
    setProgress(null);
    setErrors(problems);
  };

  const remove = async (image: ListingImage) => {
    const { error } = await api.DELETE("/api/v1/listings/{listingId}/images/{imageId}", {
      params: { path: { listingId, imageId: image.imageId } },
    });
    if (error) setErrors([messageFor(toProblem(error).code)]);
    else onChange(images.filter((i) => i.imageId !== image.imageId));
  };

  const reorder = async ({ active, over }: DragEndEvent) => {
    if (!over || active.id === over.id) return;
    const next = arrayMove(images, images.findIndex((i) => i.imageId === active.id), images.findIndex((i) => i.imageId === over.id));
    onChange(next);
    const { error } = await api.PUT("/api/v1/listings/{listingId}/images/order", {
      params: { path: { listingId } },
      body: { imageIds: next.map((i) => i.imageId) },
    });
    if (error) {
      onChange(images);
      setErrors([messageFor(toProblem(error).code)]);
    }
  };

  return (
    <div className="flex flex-col gap-3">
      <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={reorder}>
        <SortableContext items={images.map((i) => i.imageId)} strategy={rectSortingStrategy}>
          <ul className="flex flex-wrap gap-3">
            {images.map((image, i) => (
              <SortableImage key={image.imageId} image={image} index={i} onRemove={() => void remove(image)} />
            ))}
          </ul>
        </SortableContext>
      </DndContext>
      {images.length < MAX_LISTING_IMAGES ? (
        <label className="inline-flex min-h-11 w-fit cursor-pointer items-center gap-2 rounded-lg border border-dashed border-border px-4 hover:bg-muted">
          <ImagePlus aria-hidden className="size-5" />
          Thêm ảnh ({images.length}/{MAX_LISTING_IMAGES})
          <input
            type="file"
            multiple
            accept="image/jpeg,image/png,image/webp"
            className="sr-only"
            onChange={(e) => {
              void upload(e.target.files);
              e.target.value = "";
            }}
          />
        </label>
      ) : null}
      {progress ? <p aria-live="polite" className="text-sm">{progress}</p> : null}
      {errors.length > 0 ? (
        <ul role="alert" className="text-sm text-destructive">
          {errors.map((e) => (
            <li key={e}>{e}</li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

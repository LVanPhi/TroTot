"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Star } from "lucide-react";
import { useState } from "react";
import { toast } from "sonner";

import { FormError, SubmitButton } from "@/components/domain/form-controls";
import { api } from "@/lib/api/client";
import { messageFor } from "@/lib/api/errors";
import { toProblem } from "@/lib/api/problem";
import { can, useMe } from "@/lib/auth/use-auth";
import { formatDateTime } from "@/lib/format";

/** Published reviews and, for students, the review form (US-405). */
export function Reviews({ listingId, average, count }: { listingId: string; average: number | null; count: number }) {
  const queryClient = useQueryClient();
  const { data: me } = useMe();
  const reviews = useQuery({
    queryKey: ["listing", listingId, "reviews"],
    queryFn: async () => (await api.GET("/api/v1/listings/{listingId}/reviews", { params: { path: { listingId } } })).data ?? [],
  });
  const [rating, setRating] = useState(0);
  const [body, setBody] = useState("");
  const [error, setError] = useState<string | undefined>();
  const [pending, setPending] = useState(false);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (rating < 1) return setError("Chọn số sao");
    if (body.length > 1000) return setError("Nhận xét tối đa 1.000 ký tự");
    setPending(true);
    const { error: apiError } = await api.POST("/api/v1/listings/{listingId}/reviews", {
      params: { path: { listingId } },
      body: { rating, body: body.trim() || null },
    });
    setPending(false);
    if (apiError) return setError(messageFor(toProblem(apiError).code));
    toast.success("Cảm ơn bạn đã đánh giá.");
    setError(undefined);
    await queryClient.invalidateQueries({ queryKey: ["listing", listingId, "reviews"] });
  };

  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold">
        Đánh giá {count > 0 && average != null ? `· ${String(average).replace(".", ",")}/5 (${count})` : ""}
      </h2>
      {reviews.data && reviews.data.length > 0 ? (
        <ul className="flex flex-col gap-3">
          {reviews.data.map((r) => (
            <li key={String(r.id)} className="rounded-lg border border-border p-3">
              <p className="text-sm font-medium">
                {r.reviewerName} · {Number(r.rating)}/5 sao · {formatDateTime(r.createdAt).slice(0, 10)}
              </p>
              {r.body ? <p className="mt-1">{r.body}</p> : null}
            </li>
          ))}
        </ul>
      ) : (
        <p className="text-muted-foreground">Chưa có đánh giá.</p>
      )}
      {can(me, "review.create") ? (
        <form onSubmit={submit} className="flex flex-col gap-3 rounded-lg border border-border p-3" noValidate>
          <fieldset>
            <legend className="mb-1 font-medium">Bạn chấm mấy sao?</legend>
            <div className="flex gap-1">
              {[1, 2, 3, 4, 5].map((n) => (
                <button
                  key={n}
                  type="button"
                  aria-label={`${n} sao`}
                  aria-pressed={rating === n}
                  onClick={() => setRating(n)}
                  className="inline-flex size-11 items-center justify-center rounded-lg hover:bg-muted"
                >
                  <Star aria-hidden className={`size-6 ${n <= rating ? "fill-amber-400 text-amber-500" : ""}`} />
                </button>
              ))}
            </div>
          </fieldset>
          <label htmlFor="review-body" className="font-medium">
            Nhận xét (không bắt buộc)
          </label>
          <textarea id="review-body" rows={3} className="rounded-lg border border-input p-2" value={body} onChange={(e) => setBody(e.target.value)} />
          <FormError message={error} />
          <SubmitButton pending={pending}>Gửi đánh giá</SubmitButton>
        </form>
      ) : null}
    </section>
  );
}

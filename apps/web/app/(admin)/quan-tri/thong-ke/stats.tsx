"use client";

import { useQuery } from "@tanstack/react-query";
import { useState } from "react";

import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { api } from "@/lib/api/client";

const n = (v: number | string | null | undefined) => (v == null ? null : Number(v));
const fmt = (v: number | null, digits = 1) => (v == null ? "—" : v.toLocaleString("vi-VN", { maximumFractionDigits: digits }));

/**
 * US-804 from the reporting views (refreshed nightly). Quick checks older than 30 days are deleted, so older days
 * shrink over time; the weekly export of T5.2 keeps the numbers.
 */
export function StatsPage() {
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const stats = useQuery({
    queryKey: ["admin", "stats", from, to],
    queryFn: async () => (await api.GET("/api/v1/admin/stats", { params: { query: { from: from || undefined, to: to || undefined } } })).data ?? null,
  });
  const quick = (stats.data?.scans ?? []).filter((d) => d.source === "quick_check");
  const risk = (stats.data?.risk ?? []).filter((b) => b.source === "quick_check");
  const maxRisk = Math.max(1, ...risk.map((b) => Number(b.assessmentCount)));

  return (
    <main className="flex flex-col gap-6">
      <h1 className="text-2xl font-semibold tracking-tight">Thống kê</h1>
      <p className="text-sm text-muted-foreground">
        Số liệu từ view làm mới mỗi đêm. Lượt kiểm tra nhanh quá 30 ngày đã bị xóa nên số của những ngày cũ giảm dần.
      </p>
      <div className="flex flex-wrap gap-3">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="from">Từ ngày</Label>
          <Input id="from" type="date" className="h-11" value={from} onChange={(e) => setFrom(e.target.value)} />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="to">Đến ngày</Label>
          <Input id="to" type="date" className="h-11" value={to} onChange={(e) => setTo(e.target.value)} />
        </div>
      </div>

      <section className="flex flex-col gap-2">
        <h2 className="text-lg font-semibold">Lượt Kiểm tra tin theo ngày</h2>
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-border text-left">
                <th className="p-2">Ngày</th>
                <th className="p-2 text-right">Tổng</th>
                <th className="p-2 text-right">Cao</th>
                <th className="p-2 text-right">Cần thận trọng</th>
                <th className="p-2 text-right">Thấp</th>
                <th className="p-2 text-right">Lỗi</th>
                <th className="p-2 text-right">p95 (giây)</th>
              </tr>
            </thead>
            <tbody>
              {quick.map((d) => (
                <tr key={d.day} className="border-b border-border">
                  <td className="p-2">{d.day}</td>
                  <td className="p-2 text-right tabular-nums">{fmt(n(d.scanCount), 0)}</td>
                  <td className="p-2 text-right tabular-nums">{fmt(n(d.highCount), 0)}</td>
                  <td className="p-2 text-right tabular-nums">{fmt(n(d.cautionCount), 0)}</td>
                  <td className="p-2 text-right tabular-nums">{fmt(n(d.lowCount), 0)}</td>
                  <td className="p-2 text-right tabular-nums">{fmt(n(d.failedCount), 0)}</td>
                  <td className="p-2 text-right tabular-nums">{fmt(n(d.p95Seconds))}</td>
                </tr>
              ))}
              {quick.length === 0 ? (
                <tr>
                  <td colSpan={7} className="p-2 text-muted-foreground">
                    Chưa có số liệu trong khoảng này.
                  </td>
                </tr>
              ) : null}
            </tbody>
          </table>
        </div>
      </section>

      <section className="flex flex-col gap-2">
        <h2 className="text-lg font-semibold">Thời gian duyệt (trung vị, phút)</h2>
        <ul className="flex flex-col gap-1 text-sm">
          {(stats.data?.moderation ?? []).map((d) => (
            <li key={d.day}>
              {d.day}: {fmt(n(d.decisionCount), 0)} quyết định · trung vị {fmt(n(d.medianMinutes))} phút
            </li>
          ))}
          {stats.data && stats.data.moderation.length === 0 ? <li className="text-muted-foreground">Chưa có quyết định.</li> : null}
        </ul>
      </section>

      <section className="flex flex-col gap-2">
        <h2 className="text-lg font-semibold">Phân bố điểm (Kiểm tra tin)</h2>
        <ul className="flex flex-col gap-1">
          {risk.map((b) => (
            <li key={String(b.scoreBucket)} className="grid grid-cols-[5rem_1fr_4rem] items-center gap-2 text-sm">
              <span>
                {Number(b.scoreBucket)}–{Number(b.scoreBucket) + 9}
              </span>
              <span aria-hidden className="h-3 rounded bg-foreground/60" style={{ width: `${(Number(b.assessmentCount) / maxRisk) * 100}%` }} />
              <span className="text-right tabular-nums">{fmt(n(b.assessmentCount), 0)}</span>
            </li>
          ))}
        </ul>
      </section>
    </main>
  );
}

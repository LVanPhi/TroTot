"use client";

import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { toast } from "sonner";

import { secondaryButton } from "@/components/domain/form-controls";
import { ReasonDialog } from "@/components/domain/moderation-dialogs";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { api } from "@/lib/api/client";
import { messageFor } from "@/lib/api/errors";
import { toProblem } from "@/lib/api/problem";
import { formatDateTime } from "@/lib/format";

const ROLES = { student: "Sinh viên", owner: "Chủ trọ / nhà tuyển dụng", moderator: "Cán bộ xác minh", admin: "Quản trị viên" } as const;
const KEY = ["admin", "users"] as const;

/** US-801: search, lock/unlock, grant/revoke roles, verify a phone manually (US-104 AC6). */
export function Users() {
  const queryClient = useQueryClient();
  const [q, setQ] = useState("");
  const [search, setSearch] = useState("");
  const users = useQuery({
    queryKey: [...KEY, search],
    placeholderData: keepPreviousData,
    queryFn: async () => (await api.GET("/api/v1/admin/users", { params: { query: { q: search || undefined, pageSize: 50 } } })).data ?? null,
  });

  const done = async (result: { error?: unknown; response: Response }, message: string) => {
    if (!result.response.ok) {
      toast.error(messageFor(toProblem(result.error).code));
      return toProblem(result.error).code;
    }
    toast.success(message);
    await queryClient.invalidateQueries({ queryKey: KEY });
    return null;
  };

  return (
    <main className="flex flex-col gap-4">
      <h1 className="text-2xl font-semibold tracking-tight">Người dùng</h1>
      <form
        role="search"
        className="flex items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          setSearch(q.trim());
        }}
      >
        <div className="flex flex-1 flex-col gap-1.5">
          <Label htmlFor="user-q">Email hoặc họ tên</Label>
          <Input id="user-q" className="h-11" value={q} onChange={(e) => setQ(e.target.value)} />
        </div>
        <button type="submit" className={secondaryButton}>
          Tìm
        </button>
      </form>
      <ul className="flex flex-col gap-3">
        {(users.data?.items ?? []).map((u) => {
          const id = Number(u.id);
          return (
            <li key={id} className="flex flex-col gap-2 rounded-xl border border-border p-3">
              <div className="flex flex-wrap items-baseline justify-between gap-2">
                <span className="font-medium">
                  {u.fullName} <span className="text-sm text-muted-foreground">#{id} · {u.email ?? "đã ẩn danh"}</span>
                </span>
                <span className="text-sm text-muted-foreground">
                  {u.status === "locked" ? "Đã khóa" : u.status === "anonymized" ? "Đã xóa dữ liệu" : "Hoạt động"} · tạo {formatDateTime(u.createdAt)}
                </span>
              </div>
              <p className="text-sm">
                SĐT: {u.phone ?? "chưa có"} {u.phoneVerified ? "(đã xác thực)" : ""} · Vai trò:{" "}
                {u.roles.map((r) => ROLES[r as keyof typeof ROLES] ?? r).join(", ") || "không"}
              </p>
              {u.status !== "anonymized" ? (
                <div className="flex flex-wrap gap-2">
                  {u.status === "locked" ? (
                    <button type="button" className={secondaryButton} onClick={async () => void (await done(await api.POST("/api/v1/admin/users/{id}/unlock", { params: { path: { id } } }), "Đã mở khóa."))}>
                      Mở khóa
                    </button>
                  ) : (
                    <button type="button" className={secondaryButton} onClick={async () => void (await done(await api.POST("/api/v1/admin/users/{id}/lock", { params: { path: { id } } }), "Đã khóa và đăng xuất mọi phiên."))}>
                      Khóa
                    </button>
                  )}
                  {(Object.keys(ROLES) as (keyof typeof ROLES)[]).map((role) =>
                    u.roles.includes(role) ? (
                      <button
                        key={role}
                        type="button"
                        className={secondaryButton}
                        onClick={async () => void (await done(await api.DELETE("/api/v1/admin/users/{id}/roles/{roleCode}", { params: { path: { id, roleCode: role } } }), "Đã thu hồi vai trò."))}
                      >
                        Thu hồi {ROLES[role]}
                      </button>
                    ) : (
                      <button
                        key={role}
                        type="button"
                        className={secondaryButton}
                        onClick={async () => void (await done(await api.POST("/api/v1/admin/users/{id}/roles", { params: { path: { id } }, body: { roleCode: role } }), "Đã gán vai trò."))}
                      >
                        Gán {ROLES[role]}
                      </button>
                    ),
                  )}
                  {!u.phoneVerified ? (
                    <ReasonDialog
                      trigger="Xác thực số thủ công"
                      title="Xác thực số điện thoại thủ công"
                      description="Chỉ dùng khi chưa có nhà cung cấp SMS và đã đối chiếu số với người dùng."
                      label="Số điện thoại"
                      maxLength={20}
                      confirm="Xác thực"
                      send={async (phone) => done(await api.POST("/api/v1/admin/users/{id}/phone", { params: { path: { id } }, body: { phone } }), "Đã xác thực số.")}
                    />
                  ) : null}
                </div>
              ) : null}
            </li>
          );
        })}
      </ul>
    </main>
  );
}

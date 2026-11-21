"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { toast } from "sonner";

import { FormError, SubmitButton, TextField, secondaryButton } from "@/components/domain/form-controls";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { api, type Me } from "@/lib/api/client";
import { applyProblemToForm } from "@/lib/api/errors";
import { deleteAccountSchema, fullNameSchema, type DeleteAccountValues, type FullNameValues } from "@/lib/auth/schemas";
import { session } from "@/lib/auth/session";
import { ME_QUERY_KEY, useSignOut } from "@/lib/auth/use-auth";

export function ProfileSection({ me }: { me: Me }) {
  const queryClient = useQueryClient();
  const form = useForm<FullNameValues>({ resolver: zodResolver(fullNameSchema), defaultValues: { fullName: me.fullName } });
  const { errors, isSubmitting, isDirty } = form.formState;

  async function onSubmit(values: FullNameValues) {
    const { data, error } = await api.PATCH("/api/v1/me", { body: values });
    if (error !== undefined || !data) {
      applyProblemToForm(error, form.setError, ["fullName"]);
      return;
    }
    queryClient.setQueryData(ME_QUERY_KEY, data);
    form.reset({ fullName: data.fullName });
    toast.success("Đã lưu họ tên.");
  }

  return (
    <form noValidate onSubmit={form.handleSubmit(onSubmit)} className="flex flex-col gap-3">
      <p className="text-sm text-muted-foreground">
        Email: <span className="text-foreground">{me.email}</span>
      </p>
      <TextField id="fullName" label="Họ và tên" autoComplete="name" error={errors.fullName?.message} {...form.register("fullName")} />
      <FormError message={errors.root?.server?.message} />
      <SubmitButton pending={isSubmitting} disabled={!isDirty || isSubmitting}>
        Lưu họ tên
      </SubmitButton>
    </form>
  );
}

export function SignOutButton() {
  const signOut = useSignOut();
  const router = useRouter();
  return (
    <button
      type="button"
      className={secondaryButton}
      onClick={async () => {
        await signOut();
        router.replace("/");
      }}
    >
      Đăng xuất
    </button>
  );
}

/** US-106: confirm with the password; the account is anonymized and every session ends. */
export function DeleteAccountSection() {
  const [open, setOpen] = useState(false);
  const router = useRouter();
  const queryClient = useQueryClient();
  const form = useForm<DeleteAccountValues>({ resolver: zodResolver(deleteAccountSchema), defaultValues: { password: "" } });
  const { errors, isSubmitting } = form.formState;

  async function onSubmit(values: DeleteAccountValues) {
    const { error } = await api.DELETE("/api/v1/me", { body: values });
    if (error !== undefined) {
      applyProblemToForm(error, form.setError, ["password"]);
      return;
    }
    session.signOut();
    queryClient.clear();
    setOpen(false);
    toast.success("Tài khoản đã được xóa.");
    router.replace("/");
  }

  return (
    <div className="flex flex-col gap-3">
      <p className="text-sm text-muted-foreground">
        Thông tin cá nhân (email, số điện thoại, họ tên) sẽ bị xóa, mọi tin của bạn bị gỡ. Không thể hoàn tác.
      </p>
      <button
        type="button"
        onClick={() => setOpen(true)}
        className="inline-flex min-h-11 items-center justify-center rounded-lg border border-destructive/50 px-4 font-medium text-destructive hover:bg-destructive/5 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
      >
        Xóa tài khoản
      </button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Xóa tài khoản?</DialogTitle>
            <DialogDescription>Nhập mật khẩu để xác nhận. Thao tác này không thể hoàn tác.</DialogDescription>
          </DialogHeader>
          <form noValidate onSubmit={form.handleSubmit(onSubmit)} className="flex flex-col gap-3">
            <TextField id="deletePassword" label="Mật khẩu" type="password" autoComplete="current-password" error={errors.password?.message} {...form.register("password")} />
            <FormError message={errors.root?.server?.message} />
            <DialogFooter>
              <button type="button" className={secondaryButton} onClick={() => setOpen(false)}>
                Hủy
              </button>
              <SubmitButton pending={isSubmitting}>Xóa vĩnh viễn</SubmitButton>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </div>
  );
}

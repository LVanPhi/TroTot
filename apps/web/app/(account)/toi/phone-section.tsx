"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, Info } from "lucide-react";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { toast } from "sonner";

import { FormError, SubmitButton, TextField, secondaryButton } from "@/components/domain/form-controls";
import { api, type Me } from "@/lib/api/client";
import { applyProblemToForm, messageFor } from "@/lib/api/errors";
import { formatPhone, normalizePhone, otpSchema, phoneSchema, type OtpValues, type PhoneValues } from "@/lib/auth/schemas";
import { ME_QUERY_KEY } from "@/lib/auth/use-auth";

type Step = { kind: "phone" } | { kind: "code"; phone: string } | { kind: "sms_unavailable" };

/** US-104 from the user's side: number → 6-digit SMS code → verified. Without an SMS provider: contact an admin. */
export function PhoneSection({ me }: { me: Me }) {
  const [step, setStep] = useState<Step>({ kind: "phone" });
  const [editing, setEditing] = useState(!me.phoneVerified);

  if (!editing && me.phone) {
    return (
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="flex items-center gap-2">
          <CheckCircle2 aria-hidden className="size-5 text-primary" />
          <span>
            {formatPhone(me.phone)} <span className="text-muted-foreground">(đã xác thực)</span>
          </span>
        </p>
        <button type="button" className={secondaryButton} onClick={() => setEditing(true)}>
          Đổi số
        </button>
      </div>
    );
  }
  if (step.kind === "sms_unavailable") {
    return (
      <div role="status" className="flex items-start gap-2 rounded-lg border border-border bg-muted/40 p-3">
        <Info aria-hidden className="mt-0.5 size-5 shrink-0" />
        <p>{messageFor("phone.sms_unavailable")}. Cán bộ sẽ gọi điện cho số của bạn trước khi xác thực.</p>
      </div>
    );
  }
  return step.kind === "phone" ? (
    <PhoneForm onSent={(phone) => setStep({ kind: "code", phone })} onSmsUnavailable={() => setStep({ kind: "sms_unavailable" })} />
  ) : (
    <OtpForm
      phone={step.phone}
      onBack={() => setStep({ kind: "phone" })}
      onVerified={() => {
        setEditing(false);
        setStep({ kind: "phone" });
      }}
    />
  );
}

function PhoneForm({ onSent, onSmsUnavailable }: { onSent: (phone: string) => void; onSmsUnavailable: () => void }) {
  const form = useForm<PhoneValues>({ resolver: zodResolver(phoneSchema), defaultValues: { phone: "" } });
  const { errors, isSubmitting } = form.formState;

  async function onSubmit({ phone }: PhoneValues) {
    const e164 = normalizePhone(phone)!;
    const { error } = await api.POST("/api/v1/auth/phone/otp", { body: { phone: e164 } });
    if (error !== undefined) {
      const problem = applyProblemToForm(error, form.setError, ["phone"]);
      if (problem.code === "phone.sms_unavailable") onSmsUnavailable();
      return;
    }
    toast.success("Đã gửi mã tới số điện thoại của bạn.");
    onSent(e164);
  }

  return (
    <form noValidate onSubmit={form.handleSubmit(onSubmit)} className="flex flex-col gap-3">
      <TextField
        id="phone"
        label="Số điện thoại di động"
        type="tel"
        inputMode="tel"
        autoComplete="tel"
        placeholder="0912 345 678"
        error={errors.phone?.message}
        {...form.register("phone")}
      />
      <FormError message={errors.root?.server?.message} />
      <SubmitButton pending={isSubmitting}>Gửi mã xác thực</SubmitButton>
    </form>
  );
}

function OtpForm({ phone, onBack, onVerified }: { phone: string; onBack: () => void; onVerified: () => void }) {
  const queryClient = useQueryClient();
  const form = useForm<OtpValues>({ resolver: zodResolver(otpSchema), defaultValues: { code: "" } });
  const { errors, isSubmitting } = form.formState;

  async function onSubmit({ code }: OtpValues) {
    const { error } = await api.POST("/api/v1/auth/phone/verify", { body: { phone, code } });
    if (error !== undefined) {
      applyProblemToForm(error, form.setError, ["code"]);
      return;
    }
    toast.success("Số điện thoại đã được xác thực.");
    await queryClient.invalidateQueries({ queryKey: ME_QUERY_KEY });
    onVerified();
  }

  return (
    <form noValidate onSubmit={form.handleSubmit(onSubmit)} className="flex flex-col gap-3">
      <p>
        Nhập mã 6 chữ số vừa gửi tới <strong>{formatPhone(phone)}</strong> (hiệu lực 5 phút).
      </p>
      <TextField id="otp" label="Mã xác thực" inputMode="numeric" autoComplete="one-time-code" maxLength={6} error={errors.code?.message} {...form.register("code")} />
      <FormError message={errors.root?.server?.message} />
      <SubmitButton pending={isSubmitting}>Xác thực số</SubmitButton>
      <button type="button" className={secondaryButton} onClick={onBack}>
        Nhập số khác
      </button>
    </form>
  );
}

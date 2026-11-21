import type { Metadata } from "next";

import { AuthCard } from "@/components/domain/form-controls";

import { RegisterForm } from "./register-form";

export const metadata: Metadata = { title: "Đăng ký" };

export default function RegisterPage() {
  return (
    <AuthCard title="Tạo tài khoản" description="Sau khi đăng ký, chúng tôi gửi mã 6 chữ số tới email của bạn.">
      <RegisterForm />
    </AuthCard>
  );
}

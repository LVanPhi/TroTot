import type { Metadata, Viewport } from "next";
import { Be_Vietnam_Pro } from "next/font/google";

import { Toaster } from "sonner";

import { SiteHeader } from "@/components/domain/site-header";
import { QueryProvider } from "@/components/providers/query-provider";
import { ServiceWorkerRegistration } from "@/components/providers/service-worker";
import { AuthProvider } from "@/lib/auth/use-auth";

import "./globals.css";

// Designed for Vietnamese diacritics; next/font self-hosts it, so browsers never call Google.
const beVietnamPro = Be_Vietnam_Pro({
  variable: "--font-sans",
  subsets: ["latin", "vietnamese"],
  weight: ["400", "500", "600", "700"],
  display: "swap",
});

export const metadata: Metadata = {
  title: {
    default: "Trọ & Việc Sạch",
    template: "%s · Trọ & Việc Sạch",
  },
  description:
    "Tìm phòng trọ và việc làm thêm đã được xác minh cho sinh viên, kèm công cụ kiểm tra dấu hiệu rủi ro của tin đăng.",
  applicationName: "Trọ & Việc Sạch",
};

export const viewport: Viewport = {
  width: "device-width",
  initialScale: 1,
  themeColor: "#0f766e",
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html lang="vi" className={`${beVietnamPro.variable} h-full antialiased`}>
      <body className="flex min-h-full flex-col bg-background font-sans text-foreground">
        <QueryProvider>
          <AuthProvider>
            <SiteHeader />
            {children}
            <Toaster richColors position="top-center" />
            <ServiceWorkerRegistration />
          </AuthProvider>
        </QueryProvider>
      </body>
    </html>
  );
}

import type { MetadataRoute } from "next";

/** PWA manifest (docs/06-frontend.md, PWA), with the Web Share Target used by /kiem-tra. */
export default function manifest(): MetadataRoute.Manifest & { share_target: unknown } {
  return {
    name: "Trọ & Việc Sạch",
    short_name: "Trọ & Việc",
    description: "Phòng trọ và việc làm thêm đã xác minh cho sinh viên",
    lang: "vi",
    start_url: "/",
    display: "standalone",
    background_color: "#ffffff",
    theme_color: "#0f766e",
    icons: [
      { src: "/icons/icon-192.png", sizes: "192x192", type: "image/png" },
      { src: "/icons/icon-512.png", sizes: "512x512", type: "image/png" },
      { src: "/icons/icon-maskable-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
    ],
    share_target: {
      action: "/kiem-tra/chia-se",
      method: "POST",
      enctype: "multipart/form-data",
      params: {
        title: "title",
        text: "text",
        url: "url",
        files: [{ name: "images", accept: ["image/jpeg", "image/png", "image/webp"] }],
      },
    },
  };
}

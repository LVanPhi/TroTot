import type { MetadataRoute } from "next";

/** Minimal PWA manifest; icons, service worker and share target come with the PWA task. */
export default function manifest(): MetadataRoute.Manifest {
  return {
    name: "Trọ & Việc Sạch",
    short_name: "Trọ & Việc",
    description: "Phòng trọ và việc làm thêm đã xác minh cho sinh viên",
    lang: "vi",
    start_url: "/",
    display: "standalone",
    background_color: "#ffffff",
    theme_color: "#0f766e",
  };
}

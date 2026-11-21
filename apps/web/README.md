# troviec-web

Giao diện PWA của Trọ & Việc Sạch (Next.js 16, App Router). Thiết kế: `docs/06-frontend.md`.

```bash
pnpm install
pnpm dev          # http://localhost:3000
pnpm test         # Vitest
pnpm lint && pnpm typecheck
pnpm build
pnpm gen:api      # sinh lib/api/schema.d.ts từ API đang chạy; OPENAPI_URL đổi địa chỉ (mặc định cổng 8080)
```

`lib/api/schema.d.ts` là file sinh tự động, không sửa tay. `AGENTS.md` trong thư mục này do `next dev` tự thêm.

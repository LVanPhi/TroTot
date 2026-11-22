/*
 * Hand-written service worker (docs/06-frontend.md, PWA). Registered in production only.
 *  - precaches /offline; navigations are network-first and fall back to /offline;
 *  - never caches /api/* (including document links) or /hubs/*; everything else goes to the network as usual;
 *  - Web Share Target: POST /kiem-tra/chia-se is stored in Cache Storage "share-target" and the page reads it.
 * Keep SHARE_* names in sync with lib/scans/share-cache.ts.
 */
const VERSION = "v1";
const OFFLINE_CACHE = `offline-${VERSION}`;
const OFFLINE_URL = "/offline";
const SHARE_CACHE = "share-target";
const SHARE_TEXT_URL = "/share-target/text";
const SHARE_IMAGE_PREFIX = "/share-target/image-";
const SHARE_IMAGE_TYPES = ["image/jpeg", "image/png", "image/webp"];
const MAX_SHARED_IMAGES = 5;

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(OFFLINE_CACHE).then((cache) => cache.add(OFFLINE_URL)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) => Promise.all(keys.filter((k) => k.startsWith("offline-") && k !== OFFLINE_CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  );
});

async function storeShare(request) {
  const form = await request.formData();
  await caches.delete(SHARE_CACHE);
  const cache = await caches.open(SHARE_CACHE);
  const text = { title: form.get("title"), text: form.get("text"), url: form.get("url") }; // url is text only, never fetched
  await cache.put(SHARE_TEXT_URL, new Response(JSON.stringify(text), { headers: { "Content-Type": "application/json" } }));
  const files = form.getAll("images").filter((f) => f instanceof File && SHARE_IMAGE_TYPES.includes(f.type));
  await Promise.all(
    files.slice(0, MAX_SHARED_IMAGES).map((file, i) =>
      cache.put(
        `${SHARE_IMAGE_PREFIX}${i}`,
        new Response(file, { headers: { "Content-Type": file.type, "X-File-Name": encodeURIComponent(file.name || `anh-${i + 1}`) } }),
      ),
    ),
  );
  return Response.redirect("/kiem-tra?shared=1", 303);
}

self.addEventListener("fetch", (event) => {
  const { request } = event;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;

  if (request.method === "POST" && url.pathname === "/kiem-tra/chia-se") {
    event.respondWith(storeShare(request).catch(() => Response.redirect("/kiem-tra?shared=fallback", 303)));
    return;
  }
  if (url.pathname.startsWith("/api/") || url.pathname.startsWith("/hubs/")) return; // never cached
  if (request.mode === "navigate") {
    event.respondWith(fetch(request).catch(() => caches.match(OFFLINE_URL)));
  }
});

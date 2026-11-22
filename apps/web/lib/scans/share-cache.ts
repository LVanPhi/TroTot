/**
 * Hand-off between public/sw.js and /kiem-tra for the Web Share Target (docs/06-frontend.md, PWA).
 * The service worker stores what was shared in Cache Storage "share-target"; the page reads it once and deletes it.
 * Keep these names in sync with public/sw.js.
 */
export const SHARE_CACHE = "share-target";
export const SHARE_TEXT_URL = "/share-target/text";
export const SHARE_IMAGE_PREFIX = "/share-target/image-";

export interface SharedPayload {
  title: string | null;
  text: string | null;
  url: string | null;
  images: File[];
}

export async function takeSharedPayload(): Promise<SharedPayload | null> {
  if (typeof caches === "undefined") return null;
  try {
    const cache = await caches.open(SHARE_CACHE);
    const textResponse = await cache.match(SHARE_TEXT_URL);
    const parts = textResponse ? ((await textResponse.json()) as Partial<SharedPayload>) : {};
    const images: File[] = [];
    for (const request of await cache.keys()) {
      const path = new URL(request.url).pathname;
      if (!path.startsWith(SHARE_IMAGE_PREFIX)) continue;
      const response = await cache.match(request);
      if (!response) continue;
      const blob = await response.blob();
      const header = response.headers.get("X-File-Name");
      const name = header ? decodeURIComponent(header) : `anh-${images.length + 1}`;
      images.push(new File([blob], name, { type: blob.type }));
    }
    await caches.delete(SHARE_CACHE);
    return { title: parts.title ?? null, text: parts.text ?? null, url: parts.url ?? null, images };
  } catch {
    return null;
  }
}

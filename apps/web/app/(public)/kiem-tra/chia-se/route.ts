/**
 * Web Share Target fallback when the service worker is not running yet (docs/06-frontend.md, PWA).
 * Nothing is stored or fetched on the server: the shared files are dropped and the user is asked to pick them again.
 * The shared text is not carried in the URL either (no personal data in query strings).
 */
export function POST(request: Request): Response {
  return Response.redirect(new URL("/kiem-tra?shared=fallback", request.url), 303);
}

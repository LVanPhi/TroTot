/**
 * API client for Server Components (SSR of public pages); import it only from server code. Uses API_INTERNAL_URL
 * (the api container on the Docker network) when set, else the public base URL. Anonymous: personal fields
 * (contactPhone, isSaved) are loaded on the client.
 */
import createClient from "openapi-fetch";

import type { paths } from "./schema";

export const serverApi = createClient<paths>({
  baseUrl: process.env.API_INTERNAL_URL ?? process.env.NEXT_PUBLIC_API_BASE_URL ?? "",
});

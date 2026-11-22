/**
 * /hubs/moderation (docs/04-api.md, SignalR): QueueChanged {pendingCount}, ListingUpdated {listingId, status, version}.
 * Requires listing.moderate; the access token goes in the query string (SignalR standard).
 */
import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";

import { API_BASE_URL } from "@/lib/api/client";
import { session } from "@/lib/auth/session";

export interface ModerationEvents {
  onQueueChanged: (pendingCount: number) => void;
  onListingUpdated: (update: { listingId: string; status: string; version: number }) => void;
}

export function connectModeration(events: ModerationEvents): { stop: () => Promise<void> } {
  const connection = new HubConnectionBuilder()
    .withUrl(`${API_BASE_URL}/hubs/moderation`, { accessTokenFactory: () => session.accessToken() ?? "" })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.None)
    .build();
  connection.on("QueueChanged", (e: { pendingCount: number }) => events.onQueueChanged(e.pendingCount));
  connection.on("ListingUpdated", events.onListingUpdated);
  void connection.start().catch(() => undefined); // the pages still work by refetching
  return {
    stop: async () => {
      if (connection.state !== HubConnectionState.Disconnected) await connection.stop();
    },
  };
}

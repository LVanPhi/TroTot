/**
 * /hubs/scans (docs/04-api.md, SignalR). The hub only says "scan X changed"; the page then GETs /scans/{id}.
 * The connection is opened per result page and closed when the page unmounts.
 */
import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";

import { API_BASE_URL } from "@/lib/api/client";
import { session } from "@/lib/auth/session";

export interface ScanUpdated {
  scanId: string;
  status: string;
}

export interface ScanWatch {
  stop: () => Promise<void>;
}

/**
 * Connects, calls Watch(scanId) and reports updates. `onConnectionChange(false)` means updates may be missed
 * (connect failed, connection dropped) so the caller should poll; `true` once watching again.
 */
export function watchScan(
  scanId: string,
  onUpdate: (update: ScanUpdated) => void,
  onConnectionChange: (connected: boolean) => void,
): ScanWatch {
  const connection = new HubConnectionBuilder()
    .withUrl(`${API_BASE_URL}/hubs/scans`, { accessTokenFactory: () => session.accessToken() ?? "" })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.None)
    .build();

  let stopped = false;
  const watch = async () => {
    try {
      await connection.invoke("Watch", scanId);
      onConnectionChange(true);
    } catch {
      onConnectionChange(false);
    }
  };

  connection.on("ScanUpdated", (update: ScanUpdated) => {
    if (update.scanId === scanId) onUpdate(update);
  });
  connection.onreconnecting(() => onConnectionChange(false));
  connection.onreconnected(() => void watch()); // groups are per connection: join again
  connection.onclose(() => {
    if (!stopped) onConnectionChange(false);
  });

  void connection
    .start()
    .then(watch)
    .catch(() => onConnectionChange(false));

  return {
    stop: async () => {
      stopped = true;
      if (connection.state !== HubConnectionState.Disconnected) await connection.stop();
    },
  };
}

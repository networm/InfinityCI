import { HubConnection, HubConnectionBuilder, LogLevel } from "@microsoft/signalr";

let connectionPromise: Promise<HubConnection> | null = null;

/**
 * One SignalR connection per browser tab.
 *
 * This module instance lives in the tab's own JS runtime, so every tab gets an
 * independent connection with its own ConnectionId and its own subscriptions —
 * tabs viewing different builds never affect each other. Do NOT move this into
 * a SharedWorker or storage-shared singleton; that would couple tabs together.
 */
export function getCiHub(): Promise<HubConnection> {
  connectionPromise ??= buildAndStart();
  return connectionPromise;
}

async function buildAndStart(): Promise<HubConnection> {
  const conn = new HubConnectionBuilder()
    .withUrl("/hubs/ci")
    .withAutomaticReconnect([0, 2000, 5000, 10_000, 30_000])
    .configureLogging(LogLevel.Warning)
    .build();
  await conn.start();
  return conn;
}

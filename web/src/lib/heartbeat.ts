import { HubConnectionState } from "@microsoft/signalr";

import { getCiHub } from "./signalr";

/**
 * Heartbeat watchdog.
 *
 * While healthy it pings the hub every HEALTHY_INTERVAL_MS. On a failure it
 * retries with exponential backoff (1s, 2s, 4s, 8s, 16s); each failed attempt
 * also nudges the connection to restart. After the 5th consecutive failure it
 * gives up on in-place recovery and reloads the page, which rebuilds the
 * session, connection and every page snapshot from scratch.
 */

const HEALTHY_INTERVAL_MS = 15_000;
const PING_TIMEOUT_MS = 5_000;
const BACKOFF_MS = [1_000, 2_000, 4_000, 8_000, 16_000];
const MAX_CONSECUTIVE_FAILURES = 5;

let timer: ReturnType<typeof setTimeout> | null = null;
let failures = 0;

/** Starts the watchdog once; intended to be called after login (the hub
 * requires an authenticated session, so it is not started on the login page). */
export function startHeartbeat(): void {
  if (timer !== null) return;
  schedule(HEALTHY_INTERVAL_MS);
}

function schedule(delayMs: number): void {
  timer = setTimeout(() => {
    timer = null;
    void tick();
  }, delayMs);
}

async function tick(): Promise<void> {
  if (await probe()) {
    failures = 0;
    schedule(HEALTHY_INTERVAL_MS);
    return;
  }

  failures += 1;
  if (failures >= MAX_CONSECUTIVE_FAILURES) {
    window.location.reload();
    return;
  }
  schedule(BACKOFF_MS[Math.min(failures, BACKOFF_MS.length) - 1]);
}

async function probe(): Promise<boolean> {
  try {
    const conn = await getCiHub();
    if (conn.state !== HubConnectionState.Connected) return false;
    await withTimeout(conn.invoke("Ping"), PING_TIMEOUT_MS);
    return true;
  } catch {
    return false;
  }
}

function withTimeout(promise: Promise<unknown>, ms: number): Promise<unknown> {
  return new Promise((resolve, reject) => {
    const handle = setTimeout(() => reject(new Error("heartbeat ping timed out")), ms);
    promise.then(
      (value) => {
        clearTimeout(handle);
        resolve(value);
      },
      (err) => {
        clearTimeout(handle);
        reject(err);
      },
    );
  });
}

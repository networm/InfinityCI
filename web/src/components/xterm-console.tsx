import { useEffect, useRef } from "react";
import { FitAddon } from "@xterm/addon-fit";
import { Terminal } from "@xterm/xterm";
import type { ITheme } from "@xterm/xterm";
import "@xterm/xterm/css/xterm.css";

import { formatLogTimestamp } from "@/lib/format";
import type { LogLine } from "@/lib/types";

/**
 * Terminal-style console for one build step: feeds log lines into an xterm.js
 * instance, which renders embedded ANSI SGR sequences (colors, bold, ...).
 *
 * GitHub-Actions-raw-log style: the terminal is fully expanded (one row per
 * line, capped at the last MAX_LINES lines — xterm's scrollback trims older
 * output), so scrolling happens on the page instead of inside the console.
 * While the reader stays at the page bottom, newly appended lines keep the
 * view pinned to the end of the log.
 */

// Row height mirrors the fontSize/lineHeight options below.
const LINE_HEIGHT_PX = 12 * 1.6;
const MAX_LINES = 1000;
const MIN_LINES = 3;

function pageNearBottom(margin = 80): boolean {
  return window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - margin;
}

function scrollPageToBottom(): void {
  window.scrollTo({ top: document.documentElement.scrollHeight });
}

// GitHub-dark-friendly palette matching the console background (#0d1117).
const THEME: ITheme = {
  background: "#0d1117",
  foreground: "#c9d1d9",
  cursor: "#0d1117",
  selectionBackground: "#264f78",
  black: "#6e7681",
  red: "#ff7b72",
  green: "#7ee787",
  yellow: "#e3b341",
  blue: "#79c0ff",
  magenta: "#d2a8ff",
  cyan: "#56d4dd",
  white: "#c9d1d9",
  brightBlack: "#8b949e",
  brightRed: "#ffa198",
  brightGreen: "#aff5b4",
  brightYellow: "#f2cc60",
  brightBlue: "#a5d6ff",
  brightMagenta: "#f778ba",
  brightCyan: "#76e3ea",
  brightWhite: "#ffffff",
};

export function XtermConsole({ lines, live = false }: { lines: LogLine[]; live?: boolean }) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const termRef = useRef<Terminal | null>(null);
  const writtenCount = useRef(0);
  const liveRef = useRef(live);
  liveRef.current = live;

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;

    const term = new Terminal({
      convertEol: false,
      cursorBlink: false,
      cursorInactiveStyle: "none",
      disableStdin: true,
      fontFamily:
        "ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', 'Courier New', monospace",
      fontSize: 12,
      lineHeight: 1.6,
      scrollback: MAX_LINES,
      theme: THEME,
    });
    const fit = new FitAddon();
    term.loadAddon(fit);
    // xterm selections are its own overlay, not a native DOM selection, so the
    // browser's default copy shortcut has nothing to copy — handle it here.
    term.attachCustomKeyEventHandler((event) => {
      if (event.type !== "keydown") return true;
      const mod = event.ctrlKey || event.metaKey;
      const isCopyKey =
        (mod && event.key.toLowerCase() === "c") || (event.ctrlKey && event.key === "Insert");
      if (!isCopyKey || !term.hasSelection()) return true;
      void navigator.clipboard?.writeText(term.getSelection()).catch(() => {});
      return false;
    });
    term.open(host);
    fit.fit();
    termRef.current = term;
    writtenCount.current = 0;

    const observer = new ResizeObserver(() => fit.fit());
    observer.observe(host);

    return () => {
      observer.disconnect();
      term.dispose();
      termRef.current = null;
    };
  }, []);

  useEffect(() => {
    const term = termRef.current;
    if (!term) return;
    // The parent normally only appends; a shorter array means the source was
    // swapped — clear the screen and replay instead of appending onto stale text.
    if (lines.length < writtenCount.current) {
      term.reset();
      writtenCount.current = 0;
    }
    if (lines.length === writtenCount.current) return;
    // Opening a live step jumps to the log tail; afterwards the view only
    // follows while the reader stays at the page bottom.
    const follow = (writtenCount.current === 0 && liveRef.current) || pageNearBottom();
    const payload = lines.slice(writtenCount.current).map(formatLine).join("");
    writtenCount.current = lines.length;
    term.write(payload, () => {
      if (follow) scrollPageToBottom();
    });
  }, [lines]);

  // Fully expanded: one row per log line so the page scrolls through the log.
  const rows = Math.min(Math.max(lines.length, MIN_LINES), MAX_LINES);

  return <div ref={hostRef} style={{ height: rows * LINE_HEIGHT_PX + 2 }} />;
}

/** Dim timestamp prefix, then the raw text (ANSI escape codes included). */
function formatLine(line: LogLine): string {
  return `\x1b[90m${formatLogTimestamp(line.timestampUtc)}\x1b[0m ${line.text}\r\n`;
}

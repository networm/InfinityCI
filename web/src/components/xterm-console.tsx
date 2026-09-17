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
 * The parent owns dedup/history; this component only writes lines that arrived
 * since its last flush (the initial backfill arrives as one batch).
 */

// Row height mirrors the fontSize/lineHeight options below. The window grows
// by one row per log line up to MAX_LINES; xterm's scrollback keeps only the
// last MAX_LINES lines (older output is trimmed automatically).
const LINE_HEIGHT_PX = 12 * 1.6;
const MAX_LINES = 1000;
const MIN_LINES = 3;

// GitHub-dark-friendly palette matching the console background (#0d1117).
const THEME: ITheme = {  background: "#0d1117",
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

export function XtermConsole({ lines }: { lines: LogLine[] }) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const termRef = useRef<Terminal | null>(null);
  const writtenCount = useRef(0);
  const stickToBottom = useRef(true);

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
    stickToBottom.current = true;

    const observer = new ResizeObserver(() => {
      fit.fit();
      if (stickToBottom.current) term.scrollToBottom();
    });
    observer.observe(host);

    const viewport = host.querySelector<HTMLElement>(".xterm-viewport");
    const onViewportScroll = () => {
      if (!viewport) return;
      stickToBottom.current = viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight < 40;
    };
    viewport?.addEventListener("scroll", onViewportScroll);

    return () => {
      observer.disconnect();
      viewport?.removeEventListener("scroll", onViewportScroll);
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
    const payload = lines.slice(writtenCount.current).map(formatLine).join("");
    writtenCount.current = lines.length;
    term.write(payload, () => {
      if (stickToBottom.current) term.scrollToBottom();
    });
  }, [lines]);

  // One row per log line — the window grows as output arrives; the CSS
  // max-height keeps the page usable once the terminal reaches its cap.
  const rows = Math.min(Math.max(lines.length, MIN_LINES), MAX_LINES);

  return <div ref={hostRef} style={{ height: rows * LINE_HEIGHT_PX + 2, maxHeight: "80vh" }} />;
}

/** Dim timestamp prefix, then the raw text (ANSI escape codes included). */
function formatLine(line: LogLine): string {
  return `\x1b[90m${formatLogTimestamp(line.timestampUtc)}\x1b[0m ${line.text}\r\n`;
}

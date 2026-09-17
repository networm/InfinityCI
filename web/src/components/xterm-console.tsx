import { useEffect, useRef, useState } from "react";
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

// Fallback row height; the real value is measured from the rendered rows —
// it depends on font metrics, not just fontSize × lineHeight.
const LINE_HEIGHT_PX = 12 * 1.6;
const MAX_LINES = 1000;
const MIN_LINES = 3;
// Fractional cell heights / font rounding can leave the xterm grid a row or
// two short of the buffer, which shows an internal scrollbar. Oversizing the
// host by this factor guarantees the grid always covers the buffer; the extra
// rows are invisible against the console background.
const HEIGHT_SAFETY = 1.03;

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
  // xterm 6 paints a VS Code-style scrollbar inside the terminal; scrolling is
  // page-level here, so the slider is transparent (the supported way to hide it).
  scrollbarSliderBackground: "transparent",
  scrollbarSliderHoverBackground: "transparent",
  scrollbarSliderActiveBackground: "transparent",
};

export function XtermConsole({ lines, live = false }: { lines: LogLine[]; live?: boolean }) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const termRef = useRef<Terminal | null>(null);
  const writtenCount = useRef(0);
  const liveRef = useRef(live);
  liveRef.current = live;
  const [rowHeight, setRowHeight] = useState(LINE_HEIGHT_PX);

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

    // Measure the real rendered row height so the host height matches the
    // buffer exactly — a stale estimate makes the buffer outgrow the grid and
    // an internal scrollbar appears inside the terminal.
    const measureRowHeight = () => {
      const row = host.querySelector<HTMLElement>(".xterm-rows div");
      const height = row ? row.getBoundingClientRect().height : 0;
      if (height > 0) setRowHeight((prev) => (Math.abs(prev - height) > 0.01 ? height : prev));
    };
    measureRowHeight();

    const observer = new ResizeObserver(() => {
      fit.fit();
      measureRowHeight();
    });
    observer.observe(host);
    // Row divs may only be laid out after the first paint.
    const measureTimer = window.setTimeout(measureRowHeight, 60);

    // The log is fully expanded and scrolls with the page. Left unhandled,
    // xterm consumes wheel events to scroll its own (hidden) internal buffer;
    // stopping propagation in the capture phase keeps them for the browser,
    // whose default action scrolls the page instead.
    const blockWheel = (event: WheelEvent) => event.stopPropagation();
    host.addEventListener("wheel", blockWheel, { capture: true, passive: true });

    // Keep keyboard focus on the page: if the click-focus reaches xterm's
    // hidden textarea, Home/End/PageUp/PageDown/Ctrl+F stop working. Selection
    // still works (xterm tracks the drag itself), and Ctrl+C for a terminal
    // selection is served by the document-level copy listener below.
    const preventFocus = (event: MouseEvent) => event.preventDefault();
    host.addEventListener("mousedown", preventFocus);
    const stealFocusBack = (event: FocusEvent) => {
      const target = event.target as HTMLElement | null;
      if (target?.classList.contains("xterm-helper-textarea")) target.blur();
    };
    host.addEventListener("focusin", stealFocusBack, true);

    // Ctrl+C / Edit>Copy for an xterm selection — the terminal never holds
    // focus, so the browser copy event fires on the body.
    const onCopy = (event: ClipboardEvent) => {
      if (window.getSelection()?.toString()) return; // a real DOM selection wins
      if (!term.hasSelection()) return;
      event.clipboardData?.setData("text/plain", term.getSelection());
      event.preventDefault();
    };
    document.addEventListener("copy", onCopy);

    return () => {
      document.removeEventListener("copy", onCopy);
      host.removeEventListener("mousedown", preventFocus);
      host.removeEventListener("focusin", stealFocusBack, true);
      host.removeEventListener("wheel", blockWheel, { capture: true });
      window.clearTimeout(measureTimer);
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
      // Safety net: if the buffer still outgrows the grid (font/rounding
      // drift), grow the measured row height until the internal overflow —
      // the only thing that shows xterm's scrollbar — is gone.
      const viewport = hostRef.current?.querySelector<HTMLElement>(".xterm-viewport");
      if (viewport && viewport.scrollHeight - viewport.clientHeight > 2) {
        setRowHeight((prev) => prev * 1.05);
      }
    });
  }, [lines]);

  // Fully expanded: one row per log line so the page scrolls through the log.
  const rows = Math.min(Math.max(lines.length, MIN_LINES), MAX_LINES);

  return <div ref={hostRef} style={{ height: Math.ceil(rows * rowHeight * HEIGHT_SAFETY) + 2 }} />;
}

/** Dim timestamp prefix, then the raw text (ANSI escape codes included). */
function formatLine(line: LogLine): string {
  return `\x1b[90m${formatLogTimestamp(line.timestampUtc)}\x1b[0m ${line.text}\r\n`;
}

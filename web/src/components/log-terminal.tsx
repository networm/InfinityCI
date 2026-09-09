import * as React from "react";
import { FitAddon } from "@xterm/addon-fit";
import { Terminal } from "@xterm/xterm";
import "@xterm/xterm/css/xterm.css";

import { cn } from "@/lib/utils";

export interface TerminalHandle {
  writeLine(text: string): void;
  clear(): void;
}

/**
 * Read-only xterm.js view for build logs. The feed layer (per-tab SignalR +
 * offset cursor) pushes complete lines in via writeLine.
 */
export const LogTerminal = React.forwardRef<TerminalHandle, { className?: string }>(
  ({ className }, ref) => {
    const containerRef = React.useRef<HTMLDivElement>(null);
    const termRef = React.useRef<Terminal | null>(null);

    React.useEffect(() => {
      const term = new Terminal({
        convertEol: true,
        disableStdin: true,
        scrollback: 20_000,
        fontSize: 12,
        lineHeight: 1.2,
        fontFamily: "Consolas, 'Cascadia Mono', 'Courier New', monospace",
        theme: {
          background: "#101010",
          foreground: "#d4d4d4",
          selectionBackground: "#3a3d41",
        },
      });
      const fit = new FitAddon();
      term.loadAddon(fit);
      term.open(containerRef.current!);
      fit.fit();

      const observer = new ResizeObserver(() => {
        try {
          fit.fit();
        } catch {
          // container briefly unmeasurable during layout — next resize retries
        }
      });
      observer.observe(containerRef.current!);
      termRef.current = term;

      return () => {
        observer.disconnect();
        term.dispose();
        termRef.current = null;
      };
    }, []);

    React.useImperativeHandle(
      ref,
      () => ({
        writeLine: (text) => termRef.current?.writeln(text),
        clear: () => termRef.current?.clear(),
      }),
      [],
    );

    return <div ref={containerRef} className={cn("min-h-[300px] flex-1", className)} />;
  },
);
LogTerminal.displayName = "LogTerminal";

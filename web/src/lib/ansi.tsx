import { useMemo, type CSSProperties, type ReactElement } from "react";

/**
 * Renders a text line containing ANSI SGR escape sequences (colored build
 * tool output) as styled spans. Supports the common 16/256-color palettes,
 * bold, dim, italic and underline — enough for typical compiler/test output.
 */

// GitHub-dark-friendly palette matching the console background (#0d1117).
const BASIC: readonly string[] = [
  "#6e7681", // 0 black
  "#ff7b72", // 1 red
  "#7ee787", // 2 green
  "#e3b341", // 3 yellow
  "#79c0ff", // 4 blue
  "#d2a8ff", // 5 magenta
  "#56d4dd", // 6 cyan
  "#c9d1d9", // 7 white
];
const BRIGHT: readonly string[] = [
  "#8b949e",
  "#ffa198",
  "#aff5b4",
  "#f2cc60",
  "#a5d6ff",
  "#f778ba",
  "#76e3ea",
  "#ffffff",
];

function xterm256(n: number): string {
  if (n < 16) return n < 8 ? BASIC[n] : BRIGHT[n - 8];
  if (n < 232) {
    const i = n - 16;
    const steps = [0, 95, 135, 175, 215, 255];
    const r = steps[Math.floor(i / 36)];
    const g = steps[Math.floor(i / 6) % 6];
    const b = steps[i % 6];
    return `rgb(${r},${g},${b})`;
  }
  const gray = 8 + (n - 232) * 10;
  return `rgb(${gray},${gray},${gray})`;
}

interface AnsiStyle {
  color?: string;
  backgroundColor?: string;
  bold?: boolean;
  dim?: boolean;
  italic?: boolean;
  underline?: boolean;
}

function applyCodes(codes: string[], style: AnsiStyle): void {
  if (codes.length === 0 || codes.every((c) => c === "" || c === "0")) {
    for (const key of Object.keys(style) as (keyof AnsiStyle)[]) delete style[key];
    return;
  }
  for (let i = 0; i < codes.length; i++) {
    const code = Number(codes[i] || "0");
    switch (code) {
      case 0:
        for (const key of Object.keys(style) as (keyof AnsiStyle)[]) delete style[key];
        break;
      case 1:
        style.bold = true;
        style.dim = false;
        break;
      case 2:
        style.dim = true;
        style.bold = false;
        break;
      case 3:
        style.italic = true;
        break;
      case 4:
        style.underline = true;
        break;
      case 21:
      case 22:
        style.bold = false;
        style.dim = false;
        break;
      case 23:
        style.italic = false;
        break;
      case 24:
        style.underline = false;
        break;
      case 39:
        delete style.color;
        break;
      case 49:
        delete style.backgroundColor;
        break;
      case 38:
      case 48: {
        // Extended color: 38;5;n or 38;2;r;g;b
        const mode = codes[i + 1];
        let value: string | undefined;
        if (mode === "5" && i + 2 < codes.length) {
          value = xterm256(Number(codes[i + 2]));
          i += 2;
        } else if (mode === "2" && i + 4 < codes.length) {
          value = `rgb(${codes[i + 2]},${codes[i + 3]},${codes[i + 4]})`;
          i += 4;
        } else {
          break;
        }
        if (code === 38) style.color = value;
        else style.backgroundColor = value;
        break;
      }
      default:
        if (code >= 30 && code <= 37) style.color = BASIC[code - 30];
        else if (code >= 90 && code <= 97) style.color = BRIGHT[code - 90];
        else if (code >= 40 && code <= 47) style.backgroundColor = BASIC[code - 40];
        else if (code >= 100 && code <= 107) style.backgroundColor = BRIGHT[code - 100];
        break;
    }
  }
}

function toCss(style: AnsiStyle): CSSProperties {
  const css: CSSProperties = {};
  if (style.color) css.color = style.color;
  if (style.backgroundColor) css.backgroundColor = style.backgroundColor;
  if (style.bold) css.fontWeight = 600;
  if (style.dim) css.opacity = 0.65;
  if (style.italic) css.fontStyle = "italic";
  if (style.underline) css.textDecoration = "underline";
  return css;
}

/** Splits a line into styled segments; plain text keeps the default color. */
export function AnsiText({ text }: { text: string }): ReactElement {
  const segments = useMemo(() => {
    const out: { text: string; style: AnsiStyle }[] = [];
    const style: AnsiStyle = {};
    let last = 0;
    const pattern = /\x1b\[([0-9;]*)m/g;
    let match: RegExpExecArray | null;
    const flush = (end: number) => {
      if (end > last) out.push({ text: text.slice(last, end), style: { ...style } });
    };
    while ((match = pattern.exec(text)) !== null) {
      flush(match.index);
      last = match.index + match[0].length;
      applyCodes(match[1].split(";"), style);
    }
    flush(text.length);
    return out;
  }, [text]);

  return (
    <>
      {segments.map((segment, index) => {
        const css = toCss(segment.style);
        return Object.keys(css).length > 0 ? (
          <span key={index} style={css}>
            {segment.text}
          </span>
        ) : (
          <span key={index}>{segment.text}</span>
        );
      })}
    </>
  );
}

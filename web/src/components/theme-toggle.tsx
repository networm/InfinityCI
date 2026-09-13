import { Moon, Sun } from "lucide-react";
import { useState } from "react";

import { applyTheme, currentTheme, type Theme } from "@/lib/theme";

/** Sun/Moon switcher; `dark` matches the top navigation bar styling. */
export function ThemeToggle({ dark = false }: { dark?: boolean }) {
  const [theme, setTheme] = useState<Theme>(currentTheme);

  const toggle = () => {
    const next: Theme = theme === "dark" ? "light" : "dark";
    applyTheme(next);
    setTheme(next);
  };

  const className = dark
    ? "rounded-md border border-white/25 p-1.5 text-white hover:bg-white/10"
    : "rounded-md border border-line p-1.5 text-fg-muted hover:bg-hover";

  return (
    <button
      type="button"
      onClick={toggle}
      title={theme === "dark" ? "Light" : "Dark"}
      className={className}
    >
      {theme === "dark" ? <Sun size={13} /> : <Moon size={13} />}
    </button>
  );
}

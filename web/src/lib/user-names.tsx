import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";

import { api } from "@/lib/api";

/**
 * Username → display-name map (server resolves from user records; falls back
 * to the username when no display name is set). Loaded once per tab.
 */
const UserNamesContext = createContext<Record<string, string>>({});

export function UserNamesProvider({ children }: { children: ReactNode }) {
  const [names, setNames] = useState<Record<string, string>>({});

  const reload = useCallback(async () => {
    try {
      setNames(await api.userNames());
    } catch {
      // not logged in yet; retry after login via refresh call sites
    }
  }, []);

  useEffect(() => {
    void reload();
    const timer = setInterval(() => void reload(), 60_000);
    return () => clearInterval(timer);
  }, [reload]);

  return <UserNamesContext.Provider value={names}>{children}</UserNamesContext.Provider>;
}

export function useUserNames(): Record<string, string> {
  return useContext(UserNamesContext);
}

/** Resolves a username to its display name (falls back to the username itself). */
export function useResolveUserName(): (username: string | null | undefined) => string {
  const names = useUserNames();
  return (username) => {
    if (!username) return "—";
    return names[username] ?? username;
  };
}

import { Link, Outlet, useRouterState } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { LanguageToggle } from "@/components/language-toggle";
import { useMe } from "@/lib/me-context";
import { useResolveUserName } from "@/lib/user-names";

const tabs = [
  { to: "/", key: "nav.dashboard" },
  { to: "/jobs", key: "nav.jobs" },
  { to: "/runs", key: "nav.runs" },
  { to: "/agents", key: "nav.agents" },
] as const;

export function AppLayout() {
  const { me, logout } = useMe();
  const { t } = useTranslation();
  const resolveName = useResolveUserName();
  const location = useRouterState({ select: (s) => s.location.pathname });

  return (
    <div className="min-h-screen bg-[#f6f8fa] text-[#24292f]">
      <header className="bg-[#24292f] text-white">
        <div className="mx-auto flex h-14 max-w-[1280px] items-center gap-6 px-4">
          <Link to="/" className="flex items-center gap-2 text-sm font-semibold">
            <svg width="22" height="22" viewBox="0 0 16 16" fill="currentColor" aria-hidden>
              <path d="M8 0a8 8 0 0 1 8 8 8 8 0 0 1-8 8 8 8 0 0 1-8-8 8 8 0 0 1 8-8Zm0 1.5A6.5 6.5 0 1 0 14.5 8 6.5 6.5 0 0 0 8 1.5Zm2.2 3.1a.75.75 0 0 1 .2 1.04L9.14 7.6l1.86 1.96a.75.75 0 1 1-1.09 1.03L7.6 8.15a.75.75 0 0 1 0-1.03l2.4-2.52a.75.75 0 0 1 1.04-.2ZM5.5 7.25a.75.75 0 1 1 0 1.5.75.75 0 0 1 0-1.5Z" />
            </svg>
            Infinity CI
          </Link>
          <nav className="flex items-center gap-1 text-sm">
            {tabs.map((tab) => {
              const active = tab.to === "/" ? location === "/" : location.startsWith(tab.to);
              return (
                <Link
                  key={tab.to}
                  to={tab.to}
                  className={
                    "rounded-md px-3 py-1.5 transition-colors hover:bg-white/10 " +
                    (active ? "bg-white/15 font-medium" : "text-white/85")
                  }
                >
                  {t(tab.key)}
                </Link>
              );
            })}
            {me && (me.role === "Admin" || me.role === "SuperAdmin") && (
              <Link
                to="/admin"
                className={
                  "rounded-md px-3 py-1.5 transition-colors hover:bg-white/10 " +
                  (location.startsWith("/admin") ? "bg-white/15 font-medium" : "text-white/85")
                }
              >
                {t("nav.admin")}
              </Link>
            )}
          </nav>
          <div className="ml-auto flex items-center gap-3 text-sm">
            <LanguageToggle dark />
            <span className="text-white/80" title={me?.username}>
              {(me && resolveName(me.username)) || me?.username}
              <span className="ml-1.5 rounded bg-white/15 px-1.5 py-0.5 text-xs">{me?.role}</span>
            </span>
            <button
              type="button"
              onClick={() => void logout()}
              className="rounded-md border border-white/25 px-2.5 py-1 text-xs hover:bg-white/10"
            >
              {t("nav.logout")}
            </button>
          </div>
        </div>
      </header>
      <main className="mx-auto max-w-[1280px] px-4 py-6">
        <Outlet />
      </main>
    </div>
  );
}

import { StrictMode, useEffect } from "react";
import { createRoot } from "react-dom/client";
import { RouterProvider } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { router } from "./router";
import "@/lib/theme";
import "@/i18n";
import { startHeartbeat } from "./lib/heartbeat";
import { MeProvider, useMe } from "./lib/me-context";
import { UserNamesProvider } from "./lib/user-names";
import { LoginPage } from "./pages/login";
import "./styles.css";

function AppGate() {
  const { me, loading } = useMe();
  const { t } = useTranslation();

  // The hub requires an authenticated session — start the heartbeat watchdog
  // only once logged in, so the login page never accumulates failures.
  useEffect(() => {
    if (me) startHeartbeat();
  }, [me]);

  if (loading) {
    return <div className="flex min-h-screen items-center justify-center text-sm text-fg-muted">{t("common.loading")}</div>;
  }
  if (!me) {
    return <LoginPage />;
  }
  return <RouterProvider router={router} />;
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <MeProvider>
      <UserNamesProvider>
        <AppGate />
      </UserNamesProvider>
    </MeProvider>
  </StrictMode>,
);

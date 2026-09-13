import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { router } from "./router";
import "@/lib/theme";
import "@/i18n";
import { MeProvider, useMe } from "./lib/me-context";
import { UserNamesProvider } from "./lib/user-names";
import { LoginPage } from "./pages/login";
import "./styles.css";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: 1,
      refetchOnWindowFocus: false,
    },
  },
});

function AppGate() {
  const { me, loading } = useMe();
  const { t } = useTranslation();
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
    <QueryClientProvider client={queryClient}>
      <MeProvider>
        <UserNamesProvider>
          <AppGate />
        </UserNamesProvider>
      </MeProvider>
    </QueryClientProvider>
  </StrictMode>,
);

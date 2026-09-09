import { createRootRoute, createRoute, createRouter, Link, Outlet } from "@tanstack/react-router";

import { AgentsPage } from "./pages/agents";
import { BuildDetailPage } from "./pages/build-detail";
import { DashboardPage } from "./pages/dashboard";

const rootRoute = createRootRoute({
  component: () => (
    <div className="min-h-screen bg-background text-foreground">
      <header className="border-b">
        <div className="mx-auto flex h-14 max-w-6xl items-center gap-6 px-4">
          <Link to="/" className="text-sm font-semibold tracking-tight">
            Infinity CI
          </Link>
          <nav className="flex items-center gap-4 text-sm text-muted-foreground">
            <Link to="/" className="transition-colors hover:text-foreground" activeProps={{ className: "text-foreground font-medium" }}>
              Builds
            </Link>
            <Link to="/agents" className="transition-colors hover:text-foreground" activeProps={{ className: "text-foreground font-medium" }}>
              Agents
            </Link>
          </nav>
        </div>
      </header>
      <main className="mx-auto max-w-6xl px-4 py-6">
        <Outlet />
      </main>
    </div>
  ),
  notFoundComponent: () => (
    <div className="py-16 text-center text-sm text-muted-foreground">Page not found.</div>
  ),
});

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/",
  component: DashboardPage,
});

const agentsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/agents",
  component: AgentsPage,
});

const buildRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/builds/$buildId",
  component: BuildDetailPage,
});

const routeTree = rootRoute.addChildren([indexRoute, agentsRoute, buildRoute]);

export const router = createRouter({ routeTree });

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router;
  }
}

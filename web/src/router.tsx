import { createRootRoute, createRoute, createRouter } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { AppLayout } from "./layout";
import { AdminPage } from "./pages/admin";
import { AgentsPage } from "./pages/agents";
import { BuildDetailPage } from "./pages/run-detail";
import { DashboardPage } from "./pages/runs";
import { JobEditorPage } from "./pages/job-editor";
import { JobsPage } from "./pages/jobs";
import { RunsListPage } from "./pages/runs-list";
import { WorkflowDetailPage } from "./pages/workflow-detail";

function NotFound() {
  const { t } = useTranslation();
  return <div className="py-16 text-center text-sm text-[#57606a]">{t("notFound")}</div>;
}

const rootRoute = createRootRoute({
  component: AppLayout,
  notFoundComponent: NotFound,
});

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/",
  component: DashboardPage,
});

const runRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/runs/$runId",
  component: BuildDetailPage,
});

const runsListRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/runs",
  component: RunsListPage,
});

const jobsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/jobs",
  component: JobsPage,
});

const newJobRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/jobs/new",
  component: JobEditorPage,
});

const editJobRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/jobs/$name/edit",
  component: JobEditorPage,
});

const jobDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/jobs/$name",
  component: WorkflowDetailPage,
});

const agentsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/agents",
  component: AgentsPage,
});

const adminRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/admin",
  component: AdminPage,
});

const routeTree = rootRoute.addChildren([
  indexRoute,
  runRoute,
  runsListRoute,
  jobsRoute,
  newJobRoute,
  editJobRoute,
  jobDetailRoute,
  agentsRoute,
  adminRoute,
]);

export const router = createRouter({ routeTree });

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router;
  }
}

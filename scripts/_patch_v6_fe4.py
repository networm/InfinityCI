import io, sys

def patch(path, replacements, required=True):
    with io.open(path, encoding="utf-8") as f:
        s = f.read()
    for old, new in replacements:
        if old not in s:
            if required:
                print("MISS in %s: %r" % (path, old[:70]))
                sys.exit(1)
            continue
        s = s.replace(old, new)
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(s)
    print("patched", path)

TD = "web/src/pages/run-detail.tsx"

# 1) runId becomes state, resolved from (workflow, runNumber) via REST; DAG always renders.
patch(TD, [
    (
        '''  const [run, setRun] = useState<Run | null>(null);
  const [jobs, setJobs] = useState<JobRun[]>([]);''',
        '''  const [run, setRun] = useState<Run | null>(null);
  const [jobs, setJobs] = useState<JobRun[]>([]);
  // Internal run id (resolved from workflow + runNumber) drives SignalR identity.
  const [runId, setRunId] = useState<number | null>(null);''',
    ),
    (
        '''  useEffect(() => {
    let cancelled = false;
    let connection: HubConnection | null = null;

    const resubscribe = async () => {
      // Reconnects replay the full snapshot; cursors dedupe overlapping lines.
      const snapshot = await connection!.invoke<RunSubscription>("SubscribeRun", runId);''',
        '''  // Initial load: REST by (workflow, runNumber) resolves the internal run id.
  useEffect(() => {
    let cancelled = false;
    api
      .run(workflow, runNumber)
      .then((page) => {
        if (cancelled) return;
        setRunId(page.run.id);
        setRun(page.run);
        setJobs(page.jobs);
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      });
    return () => {
      cancelled = true;
    };
  }, [workflow, runNumber]);

  useEffect(() => {
    let cancelled = false;
    let connection: HubConnection | null = null;
    if (runId === null) return;

    const resubscribe = async () => {
      // Reconnects replay the full snapshot; cursors dedupe overlapping lines.
      const snapshot = await connection!.invoke<RunSubscription>("SubscribeRun", runId);''',
    ),
    (
        '''      connection?.invoke("UnsubscribeRun", runId).catch(() => {});
    };
  }, [applyJob, applyLine, applyRun, runId]);''',
        '''      connection?.invoke("UnsubscribeRun", runId).catch(() => {});
    };
  }, [applyJob, applyLine, applyRun, runId]);''',
    ),
    # header: task name links to detail; show per-workflow number
    (
        '''          <h1 className="text-xl font-semibold">
            {run?.workflowName ?? "…"} <span className="text-fg-muted">#{runId}</span>
          </h1>''',
        '''          <h1 className="text-xl font-semibold">
            <Link
              to="/jobs/$name"
              params={{ name: run?.workflowName ?? workflow }}
              className="hover:text-link hover:underline"
            >
              {run?.workflowName ?? "…"}
            </Link>{" "}
            <span className="text-fg-muted">#{run?.runNumber ?? runNumber}</span>
          </h1>''',
    ),
    (
        '''                  await api.retryRun(runId);''',
        '''                  await api.retryRun(runId);''',
    ),
    (
        '''                  await api.cancelRun(runId);''',
        '''                  await api.cancelRun(workflow, runNumber);''',
    ),
])

# imports: Link + WorkflowStore? run-detail imports needed: Link
patch(TD, [
    (
        '''import { useTranslation } from "react-i18next";''',
        '''import { useTranslation } from "react-i18next";
import { Link } from "@tanstack/react-router";''',
    ),
])

print("OK")

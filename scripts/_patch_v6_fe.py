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

R = "web/src/router.tsx"
patch(R, [
    (
        '''const runRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/runs/$runId",
  component: BuildDetailPage,
});''',
        '''const runRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/runs/$workflow/$runNumber",
  component: BuildDetailPage,
});''',
    ),
])

D = "web/src/components/task-dashboard.tsx"
patch(D, [
    (
        '''            <Link to="/runs/$runId" params={{ runId: String(item.lastRun.id) }} className="hover:text-link hover:underline">
              #{item.lastRun.id}
            </Link>''',
        '''            <Link
              to="/runs/$workflow/$runNumber"
              params={{ workflow: item.name, runNumber: String(item.lastRun.runNumber) }}
              className="hover:text-link hover:underline"
            >
              #{item.lastRun.runNumber}
            </Link>''',
    ),
    (
        '''  const trigger = (name: string) => {
    requestTrigger(name, paramDefs[name] ?? [], (run) => {
      window.location.assign(`/runs/${run.id}`);
    });
  };''',
        '''  const trigger = (name: string) => {
    requestTrigger(name, paramDefs[name] ?? [], (run) => {
      window.location.assign(`/runs/${encodeURIComponent(run.workflowName)}/${run.runNumber}`);
    });
  };''',
    ),
    (
        '''  const toggleFavorite = async (name: string) => {''',
        '''  const toggleFavorite = async (name: string) => {''',
    ),
])

RL = "web/src/pages/runs-list.tsx"
patch(RL, [
    (
        '''                  onClick={() => navigate({ to: "/runs/$runId", params: { runId: String(item.run.id) } })}''',
        '''                  onClick={() =>
                    navigate({
                      to: "/runs/$workflow/$runNumber",
                      params: { workflow: item.run.workflowName, runNumber: String(item.run.runNumber) },
                    })
                  }''',
    ),
    (
        '''                    <Link to="/runs/$runId" params={{ runId: String(item.run.id) }} className="hover:text-link hover:underline">
                      <span className="font-medium">{item.run.workflowName}</span>{" "}
                      <span className="text-[#57606a]">#{item.run.id}</span>
                    </Link>''',
        '''                    <Link
                      to="/runs/$workflow/$runNumber"
                      params={{ workflow: item.run.workflowName, runNumber: String(item.run.runNumber) }}
                      className="hover:text-link hover:underline"
                    >
                      <span className="font-medium">{item.run.workflowName}</span>{" "}
                      <span className="text-[#57606a]">#{item.run.runNumber}</span>
                    </Link>''',
    ),
])

WD = "web/src/pages/workflow-detail.tsx"
patch(WD, [
    (
        '''                    onClick={() => navigate({ to: "/runs/$runId", params: { runId: String(item.run.id) } })}''',
        '''                    onClick={() =>
                      navigate({
                        to: "/runs/$workflow/$runNumber",
                        params: { workflow: item.run.workflowName, runNumber: String(item.run.runNumber) },
                      })
                    }''',
    ),
    (
        '''                      <Link to="/runs/$runId" params={{ runId: String(item.run.id) }} className="hover:text-link hover:underline">
                        <span className="font-medium">#{item.run.id}</span>
                      </Link>''',
        '''                      <Link
                        to="/runs/$workflow/$runNumber"
                        params={{ workflow: item.run.workflowName, runNumber: String(item.run.runNumber) }}
                        className="hover:text-link hover:underline"
                      >
                        <span className="font-medium">#{item.run.runNumber}</span>
                      </Link>''',
    ),
])

TD = "web/src/pages/run-detail.tsx"
patch(TD, [
    (
        '''export function BuildDetailPage() {
  const { runId: runIdParam } = useParams({ from: "/runs/$runId" });
  const runId = Number(runIdParam);
  const resolveName = useResolveUserName();''',
        '''export function BuildDetailPage() {
  const { workflow: workflowParam, runNumber: runNumberParam } = useParams({ from: "/runs/$workflow/$runNumber" });
  const runNumber = Number(runNumberParam);
  const resolveName = useResolveUserName();''',
    ),
])

print("OK")

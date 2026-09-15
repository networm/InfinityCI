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

Q = "web/src/pages/queue.tsx"
patch(Q, [
    (
        '''                    <Link
                      to="/runs/$runId"
                      params={{ runId: String(item.runId) }}
                      className="hover:text-link hover:underline"
                    >
                      <span className="font-medium">{item.workflowName}</span>{" "}
                      <span className="text-fg-muted">#{item.runId}</span>''',
        '''                    <Link
                      to="/runs/$workflow/$runNumber"
                      params={{ workflow: item.workflowName, runNumber: String(item.runNumber) }}
                      className="hover:text-link hover:underline"
                    >
                      <span className="font-medium">{item.workflowName}</span>{" "}
                      <span className="text-fg-muted">#{item.runNumber}</span>''',
    ),
])

WD = "web/src/pages/workflow-detail.tsx"
patch(WD, [
    (
        '''                    onClick={() => navigate({ to: "/runs/$runId", params: { runId: String(item.run.id) } })}
                    className="cursor-pointer border-b border-line-muted last:border-0 hover:bg-canvas-subtle"''',
        '''                    onClick={() =>
                      navigate({
                        to: "/runs/$workflow/$runNumber",
                        params: { workflow: item.run.workflowName, runNumber: String(item.run.runNumber) },
                      })
                    }
                    className="cursor-pointer border-b border-line-muted last:border-0 hover:bg-canvas-subtle"''',
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
        '''  const { runId: runIdParam } = useParams({ from: "/runs/$runId" });
  const runId = Number(runIdParam);''',
        '''  const { workflow: workflowParam, runNumber: runNumberParam } = useParams({ from: "/runs/$workflow/$runNumber" });
  const workflow = decodeURIComponent(workflowParam);
  const runNumber = Number(runNumberParam);'''),
])

print("OK")

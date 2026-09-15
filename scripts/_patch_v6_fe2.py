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

RL = "web/src/pages/runs-list.tsx"
patch(RL, [
    (
        '''                  onClick={() => navigate({ to: "/runs/$runId", params: { runId: String(item.run.id) } })}
                  className="cursor-pointer border-b border-line-muted last:border-0 hover:bg-canvas-subtle"
                >
                  <td className="px-3 py-2">
                    <StatusIcon status={item.run.status} />
                  </td>
                  <td className="px-3 py-2">
                    <Link to="/runs/$runId" params={{ runId: String(item.run.id) }} className="hover:text-link hover:underline">
                      <span className="font-medium">{item.run.workflowName}</span>{" "}
                      <span className="text-fg-muted">#{item.run.id}</span>
                    </Link>
                  </td>''',
        '''                  onClick={() =>
                    navigate({
                      to: "/runs/$workflow/$runNumber",
                      params: { workflow: item.run.workflowName, runNumber: String(item.run.runNumber) },
                    })
                  }
                  className="cursor-pointer border-b border-line-muted last:border-0 hover:bg-canvas-subtle"
                >
                  <td className="px-3 py-2">
                    <StatusIcon status={item.run.status} />
                  </td>
                  <td className="px-3 py-2">
                    <Link
                      to="/runs/$workflow/$runNumber"
                      params={{ workflow: item.run.workflowName, runNumber: String(item.run.runNumber) }}
                      className="hover:text-link hover:underline"
                    >
                      <span className="font-medium">{item.run.workflowName}</span>{" "}
                      <span className="text-fg-muted">#{item.run.runNumber}</span>
                    </Link>
                  </td>''',
    ),
])
print("OK")

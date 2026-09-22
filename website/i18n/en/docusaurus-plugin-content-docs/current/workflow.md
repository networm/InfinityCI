---
title: Workflow Syntax
description: Complete reference for workflow YAML fields
sidebar_position: 3
---

# Workflow Syntax

Workflows are defined in YAML with syntax familiar to GitHub Actions users. Top-level fields:

```yaml
name: string              # job name (unique, also used in URLs)
project: string           # owning project
schedule: string | list   # optional, 5-field cron, e.g. "*/5 * * * *", list supported
params:                   # optional, parameterized builds, exported as env vars
  CONFIG: Release
scm:                      # optional, check out sources before running
  url: https://github.com/org/repo.git
  ref: main
  credentials: my-git     # credential name (configured on the server)
jobs:
  <job-id>:
    needs: [a, b]         # dependent jobs, forming a DAG
    runs_on: local | agent | agent:<tag>
    timeout_minutes: 30   # job-level timeout (or timeout_seconds)
    if: always()          # still run after dependencies fail (cleanup/notify jobs)
    steps:
      - name: Step name
        command: any shell command
        timeout_seconds: 600        # step-level timeout
        retry: 2                    # automatic retries on failure
        continue_on_error: true     # failure doesn't block later steps
```

## jobs

A workflow contains multiple jobs. **Jobs run in parallel by default** unless chained with `needs`:

```yaml
jobs:
  lint:
    runs_on: local
    steps:
      - name: Lint
        command: npm run lint
  build:
    needs: [lint]          # starts after lint succeeds
    runs_on: agent:windows # dispatched only to agents tagged windows
    steps:
      - name: Build
        command: npm run build
  notify:
    needs: [build]
    if: always()           # runs even when build fails
    steps:
      - name: Notify
        command: curl -X POST https://hooks.example.com/...
```

Downstream jobs of a failed dependency are skipped automatically; jobs with `if: always()` are the exception — ideal for cleanup and notifications.

## runs_on

| Value | Behavior |
| --- | --- |
| `local` | Executes in a process on the Server machine (bounded by `MaxConcurrentJobs`, default 2) |
| `agent` | Dispatched to any idle Agent |
| `agent:<tag>` | Dispatched only to Agents carrying the tag (e.g. `agent:unity`, `agent:windows`) |

## steps

Steps execute cross-platform via `ShellResolver`: cmd by default on Windows (powershell/pwsh optional), sh/bash/pwsh on Unix. Each step automatically gets:

- `CI=true`, `INFINITY_RUN_ID`, `INFINITY_JOB_KEY`
- `FORCE_COLOR=1`, `COLORTERM=truecolor`, `TERM=xterm-256color` (forced color output)
- workflow `params` and agent-level environment variables

On Windows, child process output is decoded with the console code page (e.g. GBK) so Chinese text never garbles.

Timeouts **kill the entire process tree** (no zombie Unity/compiler processes), and `retry: n` retries failed steps automatically.

## scm checkout

With an `scm:` block, sources are checked out on the executor (local or Agent) before the job runs:

```yaml
scm:
  url: https://github.com/org/repo.git
  ref: main               # branch / tag / commit
  credentials: my-git     # optional, for private repos
```

Checkout runs via the **git CLI** (identical on the Server and Agents) to handle large repositories.

## params (parameterized builds)

Keys in `params` are exported as environment variables; the manual trigger dialog lets you override values per run.

## schedule (cron triggers)

```yaml
schedule: "*/5 * * * *"            # every 5 minutes
schedule:                          # or a list
  - "0 2 * * *"                    # daily at 02:00
  - "0 9 * * 1-5"                  # weekdays at 09:00
```

5-field cron (minute hour day month weekday); the timezone follows the server (Docker deployments can set `TZ`).

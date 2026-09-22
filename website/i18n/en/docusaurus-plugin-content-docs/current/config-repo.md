---
title: Config as Git Repos
description: Config-as-code with a Git repo per job — save means commit
sidebar_position: 8
---

# Config as Git Repos

Infinity CI doesn't store job configs in a database — **each job's YAML config is its own Git repository** (`data/{job}/.git`, powered by LibGit2Sharp).

## Save means commit

Every save in the web job editor (form or YAML) automatically creates a commit. That gives you three capabilities for free:

- **History**: see who changed what, and when;
- **Rollback**: revert to any historical version with one click — a broken config no longer needs ops to the rescue;
- **Clone**: `git clone` the config repo to batch-edit locally or feed audit workflows.

## Batch management example

```bash
# Clone a job's config repo (read-only)
git clone http://ci.example.com:5000/repos/UnityCIGame.git

# Edit locally, commit, and the web UI shows the new version instantly
```

## Not to be confused with SCM checkout

| | Job config repo | Workflow `scm:` checkout |
| --- | --- | --- |
| Contents | The job's workflow YAML | Your business source code |
| Implementation | LibGit2Sharp (object-level operations, fine-grained) | git CLI (handles large repos) |
| Lives on | Server, under `data/{job}/` | The executor's workspace (local or Agent) |

Config repos need high-frequency commits, history, and diffs — LibGit2Sharp embedded in the server. Source checkout targets large repos on arbitrary executors — the git CLI. Each mechanism does what it's best at.

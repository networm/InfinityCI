---
title: Triggers
description: Manual, webhook, PR/MR, cron, and API triggering
sidebar_position: 4
---

# Triggers

Infinity CI supports five ways to trigger a run.

## Manual (web)

Click "Run" on a job page and fill in the [parameter](/docs/workflow#params-parameterized-builds) values in the trigger dialog.

## Git push webhook

Every job can expose a webhook endpoint:

```text
POST /api/webhooks/{token}
```

Features:

- **HMAC-SHA256 verification**: validates the `X-Hub-Signature-256` or `X-Signature` header to prevent forged requests
- **Branch filtering**: glob matching such as `main,release/*` — only matching branches trigger
- **Multi-platform payloads**: GitHub / GitHub Enterprise / GitLab / Gitea push-event `ref` payloads work out of the box, plus a generic JSON payload for anything else
- **PR / MR triggers**: Pull Request / Merge Request events are supported, and the PR source branch is checked out automatically

Paste the URL into your GitHub/GitLab project's webhook settings. When a run finishes, pending and final states are written back as a **commit status** (GitHub/GHE) or pipeline status (GitLab), so CI results appear directly on the PR page.

## Cron

Declare [schedule](/docs/workflow#schedule-cron-triggers) at the top level of the workflow YAML; runs fire automatically and multiple expressions are supported.

## API tokens

Each user can issue a personal API token (`Authorization: Bearer <token>`) for CLIs, scripts, or third-party systems to call the REST API as that user — trigger builds, query status, and more.

## Enable / disable

Admins can disable a job from the web UI: while disabled, no trigger (including webhooks and cron) will produce new runs.

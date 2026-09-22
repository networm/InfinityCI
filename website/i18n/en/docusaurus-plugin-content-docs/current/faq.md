---
title: FAQ
description: Frequently asked questions
sidebar_position: 11
---

# FAQ

## What's the default account?

The first start creates super admin **admin / admin** and a Default project. Change the password immediately in production.

## Do I need to install a database?

No. Storage is a **single SQLite file** (`data/infinityci.db`) — backing up means copying the `data/` directory.

## Can agents run behind NAT / on intranet machines?

Yes — that's the Agent's design scenario: it only makes **outbound** gRPC connections (to the Master's port 5001). Build machines need no inbound ports and no public IP.

## Does Chinese text garble in logs on Windows?

No. Child output is decoded with the console code page (e.g. GBK), and `FORCE_COLOR`-style variables are injected so colored output renders correctly in the xterm.js terminal.

## What if a job hangs?

Job/Step-level `timeout_minutes` (or `timeout_seconds`) kills the **entire process tree** when it expires — including Unity, compilers, and other children. You can also cancel a run manually from the web UI.

## Which Git platforms are supported?

Webhook payloads work with **GitHub / GitHub Enterprise / GitLab / Gitea** (plus a generic JSON payload), and commit status is written back to GitHub/GHE and GitLab. Source checkout uses the git CLI, so any Git server (including self-hosted) works.

## How do I define a job that runs even when dependencies fail?

Add `if: always()` to the job — typical for cleanup and notification jobs. Downstream jobs of a failed dependency are skipped by default.

## Is state lost when the server restarts?

No. Everything (database, config repos, logs, workspaces) lives under `data/` and survives restarts; jobs orphaned by an offline agent are requeued automatically.

## Do pages need manual refresh?

No. Every page holds its own SignalR connection and updates live; the heartbeat watchdog reconnects with exponential backoff and auto-refreshes the page after repeated failures.

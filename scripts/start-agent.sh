#!/usr/bin/env bash
# Start an Infinity CI agent and connect it to the master.
# Usage: ./scripts/start-agent.sh [--Agent:EnrollToken=<token>] [--Agent:AgentName=name] [other Agent:* options]
set -euo pipefail
cd "$(dirname "$0")/.."

echo "Building InfinityCI.Agent ..."
dotnet build src/InfinityCI.Agent -v q

echo "Starting agent (master: http://127.0.0.1:5001) ..."
exec dotnet run --project src/InfinityCI.Agent --no-build -- "$@"

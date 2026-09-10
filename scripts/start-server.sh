#!/usr/bin/env bash
# Build and start the Infinity CI server (web: 5000, agent gRPC: 5001).
# Requires the .NET 10 SDK.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "Building InfinityCI.Server ..."
dotnet build src/InfinityCI.Server -v q

echo "Starting server on http://127.0.0.1:5000 (gRPC :5001) ..."
exec dotnet run --project src/InfinityCI.Server --no-build

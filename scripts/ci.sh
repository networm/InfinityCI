#!/usr/bin/env bash
# Local CI: restore, build, test. Exit non-zero on any failure.
set -euo pipefail
cd "$(dirname "$0")/.."

DOTNET="${DOTNET_EXE:-$HOME/.dotnet/dotnet.exe}"

"$DOTNET" restore
"$DOTNET" build --no-restore
"$DOTNET" test --no-build

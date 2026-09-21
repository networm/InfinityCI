#!/usr/bin/env bash
# Builds self-contained release archives for every supported platform.
# Output: publish/dist/InfinityCI.{Server|Agent}.{Win64|Linux64|LinuxArm64|Osx64|OsxArm64}.{zip|tar.gz}
# Unix archives are .tar.gz with executable permissions preserved. The
# win-x64 zip is written with `zip` when available (otherwise a .tar.gz).
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== Building web UI =="
(cd web && npm ci && npm run build)

RIDS=("win-x64" "linux-x64" "linux-arm64" "osx-x64" "osx-arm64")
NAMES=("Win64" "Linux64" "LinuxArm64" "Osx64" "OsxArm64")

server_start_sh='#!/usr/bin/env bash
# Infinity CI server: web UI on 0.0.0.0:5000, agent gRPC on 0.0.0.0:5001.
# Linux system dependencies: git and libldap (LDAP login).
export InfinityCI__ListenHost=0.0.0.0
./InfinityCI.Server "$@"
'

agent_start_sh='#!/usr/bin/env bash
# Infinity CI agent: pass the master address, e.g.
#   bash StartAgent.sh --Agent:MasterUrl=http://<master>:5000
./InfinityCI.Agent "$@"
'

mkdir -p publish/dist

for i in "${!RIDS[@]}"; do
    rid="${RIDS[$i]}"
    name="${NAMES[$i]}"
    server_dir="publish/$rid/server"
    agent_dir="publish/$rid/agent"

    echo "== Publishing server $rid =="
    dotnet publish src/InfinityCI.Server -c Release -r "$rid" --self-contained true \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$server_dir"
    echo "== Publishing agent $rid =="
    dotnet publish src/InfinityCI.Agent -c Release -r "$rid" --self-contained true \
        -p:PublishSingleFile=true -o "$agent_dir"

    # Bundle the built SPA into the server layout.
    mkdir -p "$server_dir/wwwroot"
    cp -r web/dist/. "$server_dir/wwwroot/"
    printf '%s' "$server_start_sh" > "$server_dir/StartServer.sh"
    printf '%s' "$agent_start_sh" > "$agent_dir/StartAgent.sh"
    chmod +x "$server_dir/StartServer.sh" "$agent_dir/StartAgent.sh"

    tar -czf "publish/dist/InfinityCI.Server.$name.tar.gz" -C "$server_dir" .
    tar -czf "publish/dist/InfinityCI.Agent.$name.tar.gz" -C "$agent_dir" .

    if [ "$rid" = "win-x64" ]; then
        if command -v zip >/dev/null 2>&1; then
            (cd "$server_dir" && zip -qr "../../dist/InfinityCI.Server.$name.zip" .)
            (cd "$agent_dir" && zip -qr "../../dist/InfinityCI.Agent.$name.zip" .)
        else
            echo "warning: zip not found; writing win-x64 as .tar.gz instead of .zip"
            tar -czf "publish/dist/InfinityCI.Server.$name.tar.gz" -C "$server_dir" .
            tar -czf "publish/dist/InfinityCI.Agent.$name.tar.gz" -C "$agent_dir" .
        fi
    fi
    echo "== $rid done =="
done

echo "All archives written to publish/dist/"

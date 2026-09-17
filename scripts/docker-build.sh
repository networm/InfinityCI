#!/usr/bin/env bash
# Build the Infinity CI server Docker image.
# Usage: scripts/docker-build.sh [tag ...]  (default: latest)
set -euo pipefail
cd "$(dirname "$0")/.."

IMAGE="infinityci-server"
TAGS=("$@")
if [ ${#TAGS[@]} -eq 0 ]; then
    TAGS=(latest)
fi

DOCKER_ARGS=()
for tag in "${TAGS[@]}"; do
    echo "Building image ${IMAGE}:${tag} ..."
    DOCKER_ARGS+=(-t "${IMAGE}:${tag}")
done

docker build "${DOCKER_ARGS[@]}" .
echo "Done. Run with: docker run -d -p 5000:5000 -p 5001:5001 -v infinityci-data:/app/data ${IMAGE}:latest"

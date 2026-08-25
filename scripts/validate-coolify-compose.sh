#!/usr/bin/env bash
set -Eeuo pipefail

readonly repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"

docker compose \
    --env-file "$repository_root/.env.example" \
    -f "$repository_root/compose.coolify.yaml" \
    config --format json |
    python3 "$repository_root/scripts/validate-coolify-compose.py" "$repository_root/scripts/download-llamacpp-model.sh"

printf 'Coolify Compose validation passed: pinned llama.cpp runtime, verified model download gates, and private CPU-only topology.\n'

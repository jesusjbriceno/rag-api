#!/usr/bin/env bash
set -Eeuo pipefail

readonly expected_ollama_host='http://ollama:11434'
readonly repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"

docker compose \
    --env-file "$repository_root/.env.example" \
    -f "$repository_root/compose.coolify.yaml" \
    config --format json |
    python3 -c '
import json
import sys

expected = sys.argv[1]
compose = json.load(sys.stdin)
model_pull = compose.get("services", {}).get("model-pull", {})
actual = model_pull.get("environment", {}).get("OLLAMA_HOST")

if actual != expected:
    raise SystemExit(
        f"model-pull must set OLLAMA_HOST to {expected!r}; resolved {actual!r}."
    )

services = compose.get("services", {})
published_ports = [name for name, service in services.items() if service.get("ports")]
if published_ports:
    raise SystemExit(f"Coolify Compose must not publish ports; found {published_ports!r}.")

networks = compose.get("networks", {})
if set(networks) != {"default"}:
    raise SystemExit(
        f"Coolify Compose must use only its implicit default network; found {sorted(networks)!r}."
    )
' "$expected_ollama_host"

printf 'Coolify Compose validation passed: model-pull targets %s without published ports or custom networks.\n' "$expected_ollama_host"

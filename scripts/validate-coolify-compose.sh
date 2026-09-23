#!/usr/bin/env bash
set -Eeuo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
readonly repository_root

# Ephemeral sentinel values for the admin service variables: they satisfy the
# `:?required` interpolation gates so both Compose views render, but they are
# never printed and never persisted. Production must supply real values.
readonly ADMIN_SENTINEL_VARS=(
    CLOUDFLARE_ACCESS__ISSUER
    CLOUDFLARE_ACCESS__AUDIENCE
    CLOUDFLARE_ACCESS__KEYS__0__KEY_ID
    CLOUDFLARE_ACCESS__KEYS__0__PUBLIC_KEY_PEM
    ADMIN_ASSERTION__ISSUER
    ADMIN_ASSERTION__AUDIENCE
    ADMIN_ASSERTION__APP_ID
    ADMIN_ASSERTION__KEY_ID
    ADMIN_ASSERTION__PRIVATE_KEY_PEM
    ADMIN_APP_AUTH__APPS__0__APP_ID
    ADMIN_APP_AUTH__APPS__0__KEY_ID
    ADMIN_APP_AUTH__APPS__0__CURRENT_SECRET
    ALLOWED_ADMIN_SUBJECTS__0
)

exported_sentinels=()
source_view=""

cleanup() {
    if [[ -n "${source_view}" ]]; then
        rm -f -- "${source_view}"
    fi
    for variable in "${exported_sentinels[@]}"; do
        unset -- "${variable}" 2>/dev/null || true
    done
}
trap cleanup EXIT

# The validator pins these structural values exactly; they carry no secret.
export ADMIN_API__BASE_URL="http://api:8080/"
export ADMINAPP_FQDN="adminapp.sentinel.invalid"
export RAG_ADMINAPP_IMAGE_REFERENCE="v0.0.0"
export ADMIN_APP_AUTH__APPS__0__PREVIOUS_SECRET=""

random_sentinel() {
    head -c 16 /dev/urandom | od -An -tx1 | tr -d ' \n'
}

for variable in "${ADMIN_SENTINEL_VARS[@]}"; do
    sentinel_value="sentinel-$(random_sentinel)"
    export "${variable}=${sentinel_value}"
    exported_sentinels+=("${variable}")
done

source_view="$(mktemp)"

# Source-preserving view: uninterpolated `${VAR:?required}` references.
docker compose \
    --env-file "$repository_root/.env.example" \
    -f "$repository_root/compose.coolify.yaml" \
    config --format json --no-interpolate > "${source_view}"

# Rendered view: interpolated values on stdin, source view on fd 3.
docker compose \
    --env-file "$repository_root/.env.example" \
    -f "$repository_root/compose.coolify.yaml" \
    config --format json |
    python3 "$repository_root/scripts/validate-coolify-compose.py" \
        "$repository_root/scripts/download-llamacpp-model.sh" \
        3< "${source_view}"

printf 'Coolify Compose validation passed: pinned llama.cpp runtime, verified model download gates, private CPU-only topology, pull-only immutable application images, and the additive admin boundary with source-preserving provenance.\n'

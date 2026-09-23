#!/usr/bin/env bash

# Verifies the admin image target of the Dockerfile.
# By default it runs the static assertions and then a full docker build +
# inspect verification of the same invariants. Pass --static to keep only the
# fast, daemon-independent assertions (useful when no Docker daemon is
# available).
#
# Invariants pinned for the admin target:
#   1. the rag account is uid/gid 10001 and the admin stage runs as it
#   2. the container port is 8080 (EXPOSE and ASPNETCORE_URLS)
#   3. runtime diagnostics are disabled (DOTNET_EnableDiagnostics=0)
#   4. OCI labels for version and revision are inherited from the runtime base

set -Eeuo pipefail

repo_root="$(git rev-parse --show-toplevel)"
dockerfile="${repo_root}/Dockerfile"

usage() {
  printf 'usage: %s [--static]\n' "${0##*/}"
}

fail() {
  printf 'test failure: %s\n' "$*" >&2
  exit 1
}

mode="full"
case "${1:-}" in
  "") ;;
  --static) mode="static" ;;
  *)
    usage >&2
    fail "unknown argument '${1:-}'"
    ;;
esac

require() {
  local description="$1"
  local pattern="$2"
  local stage="$3"

  grep -Eq -- "${pattern}" <<<"${stage}" || fail "${description}: pattern '${pattern}' not found"
}

# Prints the body of a Dockerfile stage (everything after its FROM line until
# the next FROM or EOF).
stage_body() {
  local stage="$1"
  awk -v stage="$(printf '%s' "${stage}" | tr '[:upper:]' '[:lower:]')" '
    tolower($1) == "from" {
      name = tolower($4)
      if (name == "" || tolower($3) != "as") {
        name = tolower($2)
      }
      in_stage = (name == stage)
      next
    }
    in_stage { print }
  ' "${dockerfile}"
}

runtime_stage="$(stage_body runtime)"
admin_stage="$(stage_body admin)"

[[ -n "${runtime_stage}" ]] || fail "runtime stage not found in Dockerfile"
[[ -n "${admin_stage}" ]] || fail "admin stage not found in Dockerfile"

# 1. Fixed non-root identity: uid/gid 10001 on the rag account, used by admin.
require "admin uid" '--uid[[:space:]]+10001' "${runtime_stage}"
require "admin gid" '--gid[[:space:]]+10001' "${runtime_stage}"
require "admin runtime user" '^USER[[:space:]]+rag[[:space:]]*$' "${admin_stage}"

# 2. Fixed container port 8080 for the admin target.
require "admin exposed port" '^EXPOSE[[:space:]]+8080[[:space:]]*$' "${admin_stage}"
require "admin listen url" 'ASPNETCORE_URLS=http://\+:8080' "${admin_stage}"

# 3. Runtime diagnostics stay disabled for the admin target.
require "admin diagnostics disabled" 'DOTNET_EnableDiagnostics=0' "${admin_stage}"

# 4. OCI labels are declared on the runtime base, which the admin stage inherits.
require "oci version label" 'org\.opencontainers\.image\.version=' "${runtime_stage}"
require "oci revision label" 'org\.opencontainers\.image\.revision=' "${runtime_stage}"

require "admin entrypoint" 'Rag\.AdminApp\.Host\.dll' "${admin_stage}"

printf 'static admin target verification passed\n'

if [[ "${mode}" == "full" ]]; then
  command -v docker >/dev/null 2>&1 || fail "docker is required for the default build verification (use --static to skip it)"

  publication_version="test-admin-target"
  revision="$(git rev-parse HEAD)"
  image_ref="rag-admin-target-verify:${publication_version}"

  docker build --target admin \
    --build-arg "PUBLICATION_VERSION=${publication_version}" \
    --build-arg "REVISION=${revision}" \
    --tag "${image_ref}" \
    "${repo_root}"

  cleanup() {
    docker image rm --force "${image_ref}" >/dev/null 2>&1 || true
  }
  trap cleanup EXIT

  user="$(docker inspect --format '{{.Config.User}}' "${image_ref}")"
  [[ "${user}" == "rag" ]] || fail "image user: expected 'rag', got '${user}'"
  identity="$(docker run --rm --entrypoint /usr/bin/id "${image_ref}" rag)"
  [[ "${identity}" == uid=10001*gid=10001* ]] || fail "rag identity: expected uid/gid 10001, got '${identity}'"

  docker inspect --format '{{json .Config.ExposedPorts}}' "${image_ref}" | grep -q '"8080/tcp":\s*{}' \
    || docker inspect --format '{{json .Config.ExposedPorts}}' "${image_ref}" | grep -q '"8080/tcp"' \
    || fail "image must expose 8080/tcp"

  env="$(docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "${image_ref}")"
  grep -Eq '^ASPNETCORE_URLS=http://\+:8080$' <<<"${env}" || fail "image ASPNETCORE_URLS must be http://+:8080"
  grep -Eq '^DOTNET_EnableDiagnostics=0$' <<<"${env}" || fail "image DOTNET_EnableDiagnostics must be 0"

  labels="$(docker inspect --format '{{json .Config.Labels}}' "${image_ref}")"
  grep -q "\"org.opencontainers.image.version\":\"${publication_version}\"" <<<"${labels}" \
    || fail "image OCI version label missing or wrong"
  grep -q "\"org.opencontainers.image.revision\":\"${revision}\"" <<<"${labels}" \
    || fail "image OCI revision label missing or wrong"

  printf 'docker admin target verification passed\n'
fi

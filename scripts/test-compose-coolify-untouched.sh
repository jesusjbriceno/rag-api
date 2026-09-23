#!/usr/bin/env bash

# Unit 4 guard: compose.coolify.yaml may only add the admin service on top of
# the legacy stack.
#
# Invariants:
#   1. the admin service must exist in compose.coolify.yaml
#   2. every pre-existing service definition (postgres, model-download,
#      llama-cpp, migrate, api) must remain byte-equivalent to the base ref:
#      the admin service block is stripped from the current file and the
#      remainder is compared byte-for-byte against the base file
#   3. the legacy five services must all still be present, and admin must be
#      the only addition (no extra services)
#
# The base ref defaults to develop and can be overridden with COOLIFY_BASE_REF.
# The admin service is expected to be appended after the legacy services (as
# the last service, before the top-level volumes section).

set -Eeuo pipefail

repo_root="$(git rev-parse --show-toplevel)"
compose_file="${repo_root}/compose.coolify.yaml"
base_ref="${COOLIFY_BASE_REF:-develop}"
legacy_services="postgres model-download llama-cpp migrate api"

fail() {
  printf 'test failure: %s\n' "$*" >&2
  exit 1
}

# --- 1. base reference must be readable --------------------------------------
git -C "${repo_root}" show "${base_ref}:compose.coolify.yaml" >/dev/null 2>&1 ||
  fail "base ref '${base_ref}' has no compose.coolify.yaml"

# --- 2. the admin service must exist ------------------------------------------
grep -Eq '^  admin:' "${compose_file}" ||
  fail "compose.coolify.yaml is missing the admin service"

# --- 3. legacy five-service block byte-equivalence ----------------------------
# Strip the admin service block (from its two-space 'admin:' key until the next
# service key or a top-level key) and compare the remainder byte-for-byte with
# the base file.
strip_admin_block() {
  awk '
    /^  admin:/ { skip = 1; next }
    skip && /^  [A-Za-z0-9_-]+:/ { skip = 0 }
    skip && /^[^ ]/ { skip = 0 }
    skip { next }
    { print }
  ' "${compose_file}"
}

if ! cmp -s <(strip_admin_block) <(git -C "${repo_root}" show "${base_ref}:compose.coolify.yaml"); then
  fail "legacy five-service block drifted from ${base_ref}; only the admin service may be added"
fi

# --- 4. legacy services present, admin the only addition ----------------------
for service in ${legacy_services}; do
  grep -Eq "^  ${service}:" "${compose_file}" ||
    fail "legacy service '${service}' is missing from compose.coolify.yaml"
done

# Service detection is scoped to the services section (from the top-level
# 'services:' key until 'volumes:') so top-level volume keys — indented two
# spaces under volumes: — are never mistaken for services.
extra_services="$(
  sed -n '/^services:/,/^volumes:/p' "${compose_file}" |
    grep -E '^  [A-Za-z0-9_-]+:' |
    sed -E 's/^  ([A-Za-z0-9_-]+):.*/\1/' |
    grep -vxE 'admin|postgres|model-download|llama-cpp|migrate|api' || true
)"
if [[ -n "${extra_services}" ]]; then
  fail "unexpected extra services in compose.coolify.yaml: ${extra_services}"
fi

printf 'compose.coolify.yaml: legacy five-service block byte-equivalent to %s; admin service present\n' "${base_ref}"

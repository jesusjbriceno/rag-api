#!/usr/bin/env bash

set -Eeuo pipefail

repo_root="$(git rev-parse --show-toplevel)"
script="${repo_root}/scripts/ci-prune-staging-versions.sh"

fail() {
  printf 'test failure: %s\n' "$*" >&2
  exit 1
}

assert_equals() {
  local expected="$1"
  local actual="$2"
  local message="$3"

  [[ "${actual}" == "${expected}" ]] || fail "${message}: expected '${expected}', got '${actual}'"
}

# GHCR timestamps are RFC3339 UTC ("2026-09-25T09:00:00Z"); the selector
# compares them against jq's builtin "now", so tests derive ages from the
# current clock and stay offline.
old_created_at="$(date -u -d '2 days ago' +%Y-%m-%dT%H:%M:%SZ)"
young_created_at="$(date -u -d '1 hour ago' +%Y-%m-%dT%H:%M:%SZ)"

temp_dir="$(mktemp -d)"
cleanup() {
  rm -rf "${temp_dir}"
}
trap cleanup EXIT

versions_json="${temp_dir}/versions.json"

# GHCR versions JSON array shape: id, created_at, and
# metadata.container.tags. ids are numeric in the real API.
cat >"${versions_json}" <<EOF
[
  {
    "id": 1001,
    "created_at": "${old_created_at}",
    "metadata": {"container": {"tags": ["staging-v1.0.0"]}}
  },
  {
    "id": 1002,
    "created_at": "${old_created_at}",
    "metadata": {"container": {"tags": ["v1.0.0", "staging-v1.0.0"]}}
  },
  {
    "id": 1003,
    "created_at": "${old_created_at}",
    "metadata": {"container": {"tags": ["v1.0.0", "sha256-93c58b5c559d4d25db7b8ba99da150db067203435ab625781108ad0a575df224"]}}
  },
  {
    "id": 1004,
    "created_at": "${young_created_at}",
    "metadata": {"container": {"tags": ["staging-v1.0.0"]}}
  },
  {
    "id": 1005,
    "created_at": "${old_created_at}",
    "metadata": {"container": {"tags": []}}
  },
  {
    "id": 1006,
    "created_at": "${old_created_at}",
    "metadata": {"container": {"tags": ["v0.1.0", "sha256-93c58b5c559d4d25db7b8ba99da150db067203435ab625781108ad0a575df224", "staging-v0.1.0"]}}
  }
]
EOF

# Regression: only the staging-only, old version is prunable. The version
# carrying the published release tag (1002) and the exact observed
# published shape (1006: "v0.1.0" + digest tag + "staging-v0.1.0") must
# never be selected.
assert_equals "1001" "$(bash "${script}" select <"${versions_json}")" "selection output"

# select mode makes no network call: run it with a PATH that has no gh,
# no curl, and no network tools, only jq.
offline_bin="${temp_dir}/bin"
mkdir -p "${offline_bin}"
ln -s "$(command -v jq)" "${offline_bin}/jq"
ln -s "$(command -v bash)" "${offline_bin}/bash"
offline_output="$(env -i PATH="${offline_bin}" HOME="${temp_dir}" \
  bash "${script}" select <"${versions_json}")"
assert_equals "1001" "${offline_output}" "offline selection output"

# Digest-tagged release version without a staging tag is not selected.
assert_equals "" "$(printf '%s' '[
  {"id": 2001,
   "created_at": "'"${old_created_at}"'",
   "metadata": {"container": {"tags": ["v1.0.0", "sha256-93c58b5c559d4d25db7b8ba99da150db067203435ab625781108ad0a575df224"]}}}
]' | bash "${script}" select)" "digest-tagged release version"

# Young staging-only version is not selected.
assert_equals "" "$(printf '%s' '[
  {"id": 2002,
   "created_at": "'"${young_created_at}"'",
   "metadata": {"container": {"tags": ["staging-v1.0.0"]}}}
]' | bash "${script}" select)" "young staging-only version"

# Empty tag list is not selected.
assert_equals "" "$(printf '%s' '[
  {"id": 2003,
   "created_at": "'"${old_created_at}"'",
   "metadata": {"container": {"tags": []}}}
]' | bash "${script}" select)" "empty tag list"

# Empty input is not selected.
assert_equals "" "$(printf '%s' '[]' | bash "${script}" select)" "empty versions array"

printf '%s\n' 'ci prune staging versions tests passed'

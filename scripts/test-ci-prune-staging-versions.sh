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

  [[ "${actual}" == "${expected}" ]] || fail "${message}: expected '${expected}', got '${actual}'}"
}

# GHCR timestamps are RFC3339 UTC ("2026-09-25T09:00:00Z"); the selector
# compares them against jq's builtin "now", so tests derive ages from the
# current clock and stay offline.
old_created_at="$(date -u -d '2 days ago' +%Y-%m-%dT%H:%M:%SZ)"
young_created_at="$(date -u -d '1 hour ago' +%Y-%m-%dT%H:%M:%SZ)"
ancient_created_at="$(date -u -d '60 days ago' +%Y-%m-%dT%H:%M:%SZ)"

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

# Staging regression: only the staging-only, old version is prunable. The
# version carrying the published release tag (1002) and the exact observed
# published shape (1006: "v0.1.0" + digest tag + "staging-v0.1.0") must
# never be selected.
assert_equals "1001" "$(bash "${script}" select <"${versions_json}")" "staging selection output"

# The same result via the explicit staging class name.
assert_equals "1001" "$(bash "${script}" select staging <"${versions_json}")" "explicit staging selection output"

# Develop class over the same fixture: no develop-class version exists,
# and the published release shape (1006) must never be selected.
assert_equals "" "$(bash "${script}" select develop <"${versions_json}")" "develop selection over release fixture"

# select mode makes no network call: run it with a PATH that has no gh,
# no curl, and no network tools, only jq. Both classes.
offline_bin="${temp_dir}/bin"
mkdir -p "${offline_bin}"
ln -s "$(command -v jq)" "${offline_bin}/jq"
ln -s "$(command -v bash)" "${offline_bin}/bash"
offline_output="$(env -i PATH="${offline_bin}" HOME="${temp_dir}" \
  bash "${script}" select <"${versions_json}")"
assert_equals "1001" "${offline_output}" "offline staging selection output"
offline_output="$(env -i PATH="${offline_bin}" HOME="${temp_dir}" \
  bash "${script}" select develop <"${versions_json}")"
assert_equals "" "${offline_output}" "offline develop selection output"

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

# Empty tag list is not selected (staging and develop classes).
assert_equals "" "$(printf '%s' '[
  {"id": 2003,
   "created_at": "'"${old_created_at}"'",
   "metadata": {"container": {"tags": []}}}
]' | bash "${script}" select)" "empty tag list"
assert_equals "" "$(printf '%s' '[
  {"id": 2003,
   "created_at": "'"${ancient_created_at}"'",
   "metadata": {"container": {"tags": []}}}
]' | bash "${script}" select develop)" "empty tag list (develop)"

# Empty input is not selected.
assert_equals "" "$(printf '%s' '[]' | bash "${script}" select)" "empty versions array"

# --- Develop class fixtures ---

# The exact observed develop index shape: develop-<sha>, sha256-<digest>,
# and staging-develop-<sha> on the SAME version, older than the 30-day
# threshold AND beyond the retain-20 window (21 versions, the oldest
# carries the full triple-tag shape), IS selected.
develop_index_fixture='['
ancient_epoch="$(date -u -d "${ancient_created_at}" +%s)"
for i in $(seq 1 21); do
  created="$(date -u -d "@$((ancient_epoch + (i - 1) * 3600))" +%Y-%m-%dT%H:%M:%SZ)"
  id=$((3000 + i))
  [[ "${i}" -eq 1 ]] || develop_index_fixture+=','
  develop_index_fixture+="{
    \"id\": ${id},
    \"created_at\": \"${created}\",
    \"metadata\": {\"container\": {\"tags\": [
      \"develop-0123456789abcdef0123456789abcdef${id}\",
      \"sha256-93c58b5c559d4d25db7b8ba99da150db067203435ab625781108ad0a575df224\",
      \"staging-develop-0123456789abcdef0123456789abcdef${id}\"
    ]}}
  }"
done
develop_index_fixture+=']'
assert_equals "3001" "$(printf '%s' "${develop_index_fixture}" | bash "${script}" select develop)" "develop-class index version beyond retain window"

# A version carrying a release tag (v0.1.0) plus staging-v0.1.0 is NEVER
# selected, by either class, at any age. This is the regression that
# deleted the published v0.1.0 image versions in production.
release_and_staging='[
  {"id": 3101,
   "created_at": "'"${ancient_created_at}"'",
   "metadata": {"container": {"tags": ["v0.1.0", "staging-v0.1.0"]}}}
]'
assert_equals "" "$(printf '%s' "${release_and_staging}" | bash "${script}" select)" "release-tagged version (staging class)"
assert_equals "" "$(printf '%s' "${release_and_staging}" | bash "${script}" select develop)" "release-tagged version (develop class)"

# A version carrying develop-<sha> AND a release tag is not selected, by
# either class, at any age.
develop_and_release='[
  {"id": 3102,
   "created_at": "'"${ancient_created_at}"'",
   "metadata": {"container": {"tags": ["develop-0123456789abcdef0123456789abcdef01234567", "v0.1.0"]}}}
]'
assert_equals "" "$(printf '%s' "${develop_and_release}" | bash "${script}" select)" "develop tag plus release tag (staging class)"
assert_equals "" "$(printf '%s' "${develop_and_release}" | bash "${script}" select develop)" "develop tag plus release tag (develop class)"

# A develop-class version with a tag outside the develop- / sha256- /
# staging- prefixes is not selected.
assert_equals "" "$(printf '%s' '[
  {"id": 3103,
   "created_at": "'"${ancient_created_at}"'",
   "metadata": {"container": {"tags": ["develop-0123456789abcdef0123456789abcdef01234567", "feature-x"]}}}
]' | bash "${script}" select develop)" "develop tag plus foreign tag"

# A develop-class version younger than the 30-day threshold is not
# selected even outside the retain window.
assert_equals "" "$(printf '%s' '[
  {"id": 3104,
   "created_at": "'"${old_created_at}"'",
   "metadata": {"container": {"tags": ["develop-0123456789abcdef0123456789abcdef01234567", "sha256-93c58b5c559d4d25db7b8ba99da150db067203435ab625781108ad0a575df224", "staging-develop-0123456789abcdef0123456789abcdef01234567"]}}}
]' | bash "${script}" select develop)" "young develop-class version"

# Retain-N rule: with 23 old develop-class versions, the newest 20 are
# never selected even though all are older than the threshold; only the
# 3 oldest are selected.
develop_versions='['
for i in $(seq 1 23); do
  # Oldest version is 60 days old; each next one is 1 hour younger, so
  # every version is older than the 30-day threshold.
  created="$(date -u -d "@$((ancient_epoch + (i - 1) * 3600))" +%Y-%m-%dT%H:%M:%SZ)"
  id=$((4000 + i))
  [[ "${i}" -eq 1 ]] || develop_versions+=','
  develop_versions+="{
    \"id\": ${id},
    \"created_at\": \"${created}\",
    \"metadata\": {\"container\": {\"tags\": [
      \"develop-0123456789abcdef0123456789abcdef${id}\",
      \"sha256-93c58b5c559d4d25db7b8ba99da150db067203435ab625781108ad0a575df224\",
      \"staging-develop-0123456789abcdef0123456789abcdef${id}\"
    ]}}
  }"
done
develop_versions+=']'

assert_equals "4001
4002
4003" "$(printf '%s' "${develop_versions}" | bash "${script}" select develop)" "retain-20 window over 23 old develop versions"

printf '%s\n' 'ci prune staging versions tests passed'

#!/usr/bin/env bash

set -Eeuo pipefail

repo_root="$(git rev-parse --show-toplevel)"
# shellcheck disable=SC1090
source "${repo_root}/scripts/ci-publication-marker.sh"

fail() {
  printf 'test failure: %s\n' "$*" >&2
  exit 1
}

assert_equals() {
  [[ "$1" == "$2" ]] || fail "$3: expected '$1', got '$2'"
}

temp_dir="$(mktemp -d)"
trap 'rm -rf "${temp_dir}"' EXIT
proof_dir="${temp_dir}/proofs"
mkdir -p "${proof_dir}"

write_proof() {
  local component="$1"
  local image="$2"
  jq -n --arg component "${component}" --arg image "${image}" '
    {
      component: $component,
      source_revision: "0123456789abcdef0123456789abcdef01234567",
      workflow: {run_id: "123", url: "https://github.com/example/repo/actions/runs/123"},
      publication_tag: "develop-0123456789abcdef0123456789abcdef01234567",
      signature: {
        identity: "https://github.com/example/repo/.github/workflows/ci-develop.yml@refs/heads/develop",
        issuer: "https://token.actions.githubusercontent.com"
      },
      digest_ref: ($image + "@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
      final_tag_ref: ($image + ":develop-0123456789abcdef0123456789abcdef01234567"),
      final_tag_digest: "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      attachments: {signature: "attached", spdx: "attached", slsa: "attached"}
    }
  '
}

write_proof api ghcr.io/example/rag-api >"${proof_dir}/api.json"
write_proof operator ghcr.io/example/rag-operator >"${proof_dir}/operator.json"
payload_one="$(build_completion_payload "${proof_dir}")"
payload_two="$(build_completion_payload "${proof_dir}")"
assert_equals "${payload_one}" "${payload_two}" 'marker payload idempotency'
jq -e '
  .source_revision == "0123456789abcdef0123456789abcdef01234567"
  and .workflow.run_id == "123"
  and ([.images[].digest_ref] | length == 2)
  and all(.images[]; .attachments == {signature:"attached", spdx:"attached", slsa:"attached"})
' <<<"${payload_one}" >/dev/null || fail 'marker payload content is incomplete'

existing_deployment="$(jq -nc --argjson payload "${payload_one}" '{id: 42, payload: $payload}')"
api_calls="${temp_dir}/api-calls"
gh() {
  printf '%s\n' "$*" >>"${api_calls}"
  case "$*" in
    *'/deployments?sha='*) jq -nc --argjson deployment "${existing_deployment}" '[$deployment]' ;;
    *'/deployments/42/statuses?'*) printf '%s\n' '[{"state":"success"}]' ;;
    *) fail "unexpected gh api call: $*" ;;
  esac
}
GITHUB_REPOSITORY='example/repo'
GITHUB_SHA='0123456789abcdef0123456789abcdef01234567'
GITHUB_OUTPUT="${temp_dir}/github-output"
record_completion_marker "${proof_dir}" >/dev/null
assert_equals 'marker_id=42' "$(<"${GITHUB_OUTPUT}")" 'existing marker ID'
if grep -q -- '-X POST' "${api_calls}"; then
  fail 'idempotent marker reuse created a deployment'
fi
unset -f gh

jq '.final_tag_digest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"' \
  "${proof_dir}/operator.json" >"${proof_dir}/operator-invalid.json"
mv "${proof_dir}/operator-invalid.json" "${proof_dir}/operator.json"
if (build_completion_payload "${proof_dir}" >/dev/null 2>&1); then
  fail 'marker payload accepted a final tag digest different from its image digest'
fi

write_proof operator ghcr.io/example/rag-operator >"${proof_dir}/operator.json"

rm "${proof_dir}/operator.json"
if (build_completion_payload "${proof_dir}" >/dev/null 2>&1); then
  fail 'marker payload was created after one-image failure'
fi

printf '%s\n' 'ci publication marker tests passed'

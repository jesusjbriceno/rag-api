#!/usr/bin/env bash

set -Eeuo pipefail

repo_root="$(git rev-parse --show-toplevel)"
# shellcheck disable=SC1090
source "${repo_root}/scripts/ci-publication-verifier.sh"

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

# Unit 5 AdminApp publication chain: the asynchronous verifier must prove all
# three published images (admin, api, operator): three signed indexes and six
# platforms, each with signature, SPDX, and SLSA checks. Every index and
# platform digest is unique so the test can prove each one was verified.
admin_index_digest="sha256:1111111111111111111111111111111111111111111111111111111111111111"
api_index_digest="sha256:2222222222222222222222222222222222222222222222222222222222222222"
operator_index_digest="sha256:3333333333333333333333333333333333333333333333333333333333333333"
admin_amd64_digest="sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
admin_arm64_digest="sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
api_amd64_digest="sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
api_arm64_digest="sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
operator_amd64_digest="sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
operator_arm64_digest="sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"

write_proof() {
  local component="$1"
  local image="$2"
  local index_digest="$3"
  local amd64_digest="$4"
  local arm64_digest="$5"
  jq -n \
    --arg component "${component}" \
    --arg image "${image}" \
    --arg index_digest "${index_digest}" \
    --arg amd64_digest "${amd64_digest}" \
    --arg arm64_digest "${arm64_digest}" '
    {
      component: $component,
      source_revision: "0123456789abcdef0123456789abcdef01234567",
      workflow: {run_id: "123", url: "https://github.com/example/repo/actions/runs/123"},
      publication_tag: "develop-0123456789abcdef0123456789abcdef01234567",
      signature: {
        identity: "https://github.com/example/repo/.github/workflows/ci-develop.yml@refs/heads/develop",
        issuer: "https://token.actions.githubusercontent.com"
      },
      index: {
        digest_ref: ($image + "@" + $index_digest),
        final_tag_ref: ($image + ":develop-0123456789abcdef0123456789abcdef01234567"),
        final_tag_digest: $index_digest,
        attachments: {signature: "attached", slsa: "attached"}
      },
      platforms: [
        {
          architecture: "amd64",
          digest_ref: ($image + "@" + $amd64_digest),
          tag_ref: ($image + ":develop-0123456789abcdef0123456789abcdef01234567-amd64"),
          tag_digest: $amd64_digest,
          attachments: {signature: "attached", spdx: "attached", slsa: "attached"}
        },
        {
          architecture: "arm64",
          digest_ref: ($image + "@" + $arm64_digest),
          tag_ref: ($image + ":develop-0123456789abcdef0123456789abcdef01234567-arm64"),
          tag_digest: $arm64_digest,
          attachments: {signature: "attached", spdx: "attached", slsa: "attached"}
        }
      ]
    }
  '
}

write_proof admin ghcr.io/jesusjbriceno/rag-adminapp "${admin_index_digest}" "${admin_amd64_digest}" "${admin_arm64_digest}" >"${proof_dir}/admin.json"
write_proof api ghcr.io/example/rag-api "${api_index_digest}" "${api_amd64_digest}" "${api_arm64_digest}" >"${proof_dir}/api.json"
write_proof operator ghcr.io/example/rag-operator "${operator_index_digest}" "${operator_amd64_digest}" "${operator_arm64_digest}" >"${proof_dir}/operator.json"

run_remote() {
  printf '%s\n' "$1" >>"${recorded_labels}"
  case "${test_case}" in
    verified)
      printf '%s\n' 'cosign verification output'
      return 0
      ;;
    failed)
      [[ "$1" == "async-spdx-platform-${api_amd64_digest}" ]] || return 0
      printf '%s\n' 'verification did not match the expected identity' >&2
      return 1
      ;;
    unknown-timeout)
      if [[ "$1" == "async-slsa-platform-${api_arm64_digest}" ]]; then
        return 124
      fi
      return 0
      ;;
    unknown-access)
      [[ "$1" == "async-signature-platform-${api_amd64_digest}" ]] || return 0
      printf '%s\n' 'UNAUTHORIZED: authentication required' >&2
      return 1
      ;;
    *) fail "unknown test case: ${test_case}" ;;
  esac
}

record_check() {
  recorded_result="$1"
}

assert_verified_labels() {
  local test_case="$1" label
  [[ -f "${recorded_labels}" ]] || fail "${test_case}: the verifier did not run any remote verification"
  while IFS= read -r label; do
    grep -Fxq "async-${label}" "${recorded_labels}" \
      || fail "${test_case}: the verifier did not check async-${label}"
  done <<EOF
signature-index-${admin_index_digest}
slsa-index-${admin_index_digest}
signature-platform-${admin_amd64_digest}
spdx-platform-${admin_amd64_digest}
slsa-platform-${admin_amd64_digest}
signature-platform-${admin_arm64_digest}
spdx-platform-${admin_arm64_digest}
slsa-platform-${admin_arm64_digest}
signature-index-${api_index_digest}
slsa-index-${api_index_digest}
signature-platform-${api_amd64_digest}
spdx-platform-${api_amd64_digest}
slsa-platform-${api_amd64_digest}
signature-platform-${api_arm64_digest}
spdx-platform-${api_arm64_digest}
slsa-platform-${api_arm64_digest}
signature-index-${operator_index_digest}
slsa-index-${operator_index_digest}
signature-platform-${operator_amd64_digest}
spdx-platform-${operator_amd64_digest}
slsa-platform-${operator_amd64_digest}
signature-platform-${operator_arm64_digest}
spdx-platform-${operator_arm64_digest}
slsa-platform-${operator_arm64_digest}
EOF
  local expected_count actual_count
  expected_count=24
  actual_count="$(sort -u "${recorded_labels}" | wc -l)"
  assert_equals "${expected_count}" "${actual_count}" "${test_case} distinct verification labels"
}

run_case() {
  local expected_result="$1"
  local expected_status="$2"
  test_case="$3"
  recorded_result=''
  recorded_labels="${temp_dir}/labels-${test_case}"
  rm -f "${recorded_labels}"
  GITHUB_OUTPUT="${temp_dir}/github-output-${test_case}"
  # Consumed by verify_publication (sourced productively) when writing the step summary.
  export GITHUB_STEP_SUMMARY="${temp_dir}/github-summary-${test_case}"

  if verify_publication "${proof_dir}"; then
    actual_status=0
  else
    actual_status="$?"
  fi

  assert_equals "${expected_status}" "${actual_status}" "${test_case} exit status"
  assert_equals "${expected_result}" "${recorded_result}" "${test_case} recorded result"
  assert_equals "verification_result=${expected_result}" "$(<"${GITHUB_OUTPUT}")" "${test_case} workflow output"
}

run_case verified 0 verified
assert_verified_labels verified
run_case failed 1 failed
run_case unknown 0 unknown-timeout
run_case unknown 0 unknown-access

printf '%s\n' 'ci publication verifier tests passed'

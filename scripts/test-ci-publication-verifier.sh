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

write_proof() {
  local component="$1"
  local image="$2"
  local digest="$3"
  jq -n --arg component "${component}" --arg image "${image}" --arg digest "${digest}" '
    {
      component: $component,
      source_revision: "0123456789abcdef0123456789abcdef01234567",
      workflow: {run_id: "123", url: "https://github.com/example/repo/actions/runs/123"},
      publication_tag: "develop-0123456789abcdef0123456789abcdef01234567",
      signature: {
        identity: "https://github.com/example/repo/.github/workflows/ci-develop.yml@refs/heads/develop",
        issuer: "https://token.actions.githubusercontent.com"
      },
      digest_ref: ($image + "@" + $digest),
      final_tag_ref: ($image + ":develop-0123456789abcdef0123456789abcdef01234567"),
      final_tag_digest: $digest,
      attachments: {signature: "attached", spdx: "attached", slsa: "attached"}
    }
  '
}

write_proof api ghcr.io/example/rag-api sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa >"${proof_dir}/api.json"
write_proof operator ghcr.io/example/rag-operator sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb >"${proof_dir}/operator.json"

run_remote() {
  case "${test_case}" in
    verified)
      printf '%s\n' 'cosign verification output'
      return 0
      ;;
    failed)
      [[ "$1" == async-spdx-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ]] || return 0
      printf '%s\n' 'verification did not match the expected identity' >&2
      return 1
      ;;
    unknown-timeout)
      if [[ "$1" == async-slsa-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ]]; then
        return 124
      fi
      return 0
      ;;
    unknown-access)
      [[ "$1" == async-signature-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ]] || return 0
      printf '%s\n' 'UNAUTHORIZED: authentication required' >&2
      return 1
      ;;
    *) fail "unknown test case: ${test_case}" ;;
  esac
}

record_check() {
  recorded_result="$1"
}

run_case() {
  local expected_result="$1"
  local expected_status="$2"
  test_case="$3"
  recorded_result=''
  GITHUB_OUTPUT="${temp_dir}/github-output-${test_case}"
  GITHUB_STEP_SUMMARY="${temp_dir}/github-summary-${test_case}"

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
run_case failed 1 failed
run_case unknown 0 unknown-timeout
run_case unknown 0 unknown-access

printf '%s\n' 'ci publication verifier tests passed'

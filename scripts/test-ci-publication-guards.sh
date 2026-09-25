#!/usr/bin/env bash

set -Eeuo pipefail

repo_root="$(git rev-parse --show-toplevel)"
# shellcheck disable=SC1090
source "${repo_root}/scripts/ci-publication-guards.sh"

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

temp_dir="$(mktemp -d)"
diagnostics_file="${temp_dir}/diagnostics"
exec 3>"${diagnostics_file}"
cleanup() {
  rm -rf "${temp_dir}"
}
trap cleanup EXIT

REMOTE_OPERATION_TIMEOUT="17s"
REMOTE_OPERATION_KILL_GRACE="3s"
captured_arguments="${temp_dir}/timeout-arguments"
timeout() {
  printf '%s\n' "$@" >"${captured_arguments}"
}

run_remote "safe-operation" command --sensitive-argument value
mapfile -t timeout_arguments <"${captured_arguments}"
assert_equals "--signal=TERM" "${timeout_arguments[0]}" "TERM signal argument"
assert_equals "--kill-after=3s" "${timeout_arguments[1]}" "KILL grace argument"
assert_equals "17s" "${timeout_arguments[2]}" "timeout duration argument"
assert_equals "command" "${timeout_arguments[3]}" "external command argument"
diagnostics="$(<"${diagnostics_file}")"
[[ "${diagnostics}" == *"remote-operation:safe-operation:started"* ]] || fail "missing safe start label"
[[ "${diagnostics}" == *"remote-operation:safe-operation:completed"* ]] || fail "missing completion label"
[[ "${diagnostics}" != *"sensitive-argument"* ]] || fail "operation arguments leaked to diagnostics"
unset -f timeout

REMOTE_OPERATION_TIMEOUT="1s"
REMOTE_OPERATION_KILL_GRACE="1s"
started_at="$(date +%s)"
if run_remote "timeout-operation" sleep 60 2>/dev/null; then
  fail "timeout operation unexpectedly succeeded"
else
  timeout_exit_code="$?"
fi
elapsed_seconds="$(( $(date +%s) - started_at ))"
[[ "${timeout_exit_code}" -eq 124 || "${timeout_exit_code}" -eq 137 ]] || fail "timeout returned ${timeout_exit_code} instead of a timeout status"
(( elapsed_seconds <= 4 )) || fail "timeout exceeded TERM/KILL bound (${elapsed_seconds}s)"
diagnostics="$(<"${diagnostics_file}")"
[[ "${diagnostics}" == *"remote-operation:timeout-operation:timed-out"* ]] || fail "timeout was not reported with its safe operation name"

manifest_unknown_file="${temp_dir}/manifest-unknown"
printf '%s\n' 'GET /v2/example/manifests/tag: MANIFEST_UNKNOWN: manifest unknown' >"${manifest_unknown_file}"
is_manifest_unknown_error "${manifest_unknown_file}" || fail "manifest unknown response was not accepted"

for rejected_error in \
  'UNAUTHORIZED: authentication required' \
  'dial tcp: lookup ghcr.io: no such host' \
  '404 Not Found' \
  'NAME_UNKNOWN: repository name not known to registry' \
  'malformed registry response'; do
  printf '%s\n' "${rejected_error}" >"${temp_dir}/inspection-error"
  if is_manifest_unknown_error "${temp_dir}/inspection-error"; then
    fail "non-absence error was accepted: ${rejected_error}"
  fi
done

assert_final_digest "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" \
  "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
if assert_final_digest "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" \
  "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"; then
  fail "different final digest was accepted"
fi

assert_equals "verified" "$(classify_verification_results verified verified verified)" "all verification results"
assert_equals "failed" "$(classify_verification_results verified unknown failed)" "failed verification result"
assert_equals "unknown" "$(classify_verification_results verified unknown verified)" "unknown verification result"
if classify_verification_results invalid >/dev/null; then
  fail "invalid verification result was accepted"
fi

printf '%s\n' 'ci publication guard tests passed'

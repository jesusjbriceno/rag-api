#!/usr/bin/env bash

set -Eeuo pipefail

repo_root="$(git rev-parse --show-toplevel)"
# shellcheck disable=SC1090
source "${repo_root}/scripts/ci-publication-guards.sh"
# shellcheck disable=SC1090
source "${repo_root}/scripts/ci-publication-marker.sh"

verification_result() {
  local label="$1"
  shift
  local exit_code error_file

  printf '%s\n' "::notice::async-verification:${label}:started" >&3
  error_file="$(mktemp)"
  if run_remote "async-${label}" "$@" >/dev/null 2>"${error_file}"; then
    rm -f "${error_file}"
    printf '%s\n' "::notice::async-verification:${label}:verified" >&3
    printf '%s\n' verified
    return 0
  else
    exit_code="$?"
  fi

  if [[ "${exit_code}" -eq 124 || "${exit_code}" -eq 137 ]] \
    || is_verification_access_problem "${error_file}"; then
    rm -f "${error_file}"
    printf '%s\n' "::warning::async-verification:${label}:unknown" >&3
    printf '%s\n' unknown
  else
    rm -f "${error_file}"
    printf '%s\n' "::warning::async-verification:${label}:failed" >&3
    printf '%s\n' failed
  fi
}

is_verification_access_problem() {
  local error_file="$1"

  grep -Eqi '(UNAUTHORIZED|UNAUTHENTICATED|FORBIDDEN|ACCESS DENIED|AUTHENTICATION REQUIRED|AUTHORIZATION REQUIRED|NO BASIC AUTH CREDENTIALS)' "${error_file}"
}

record_check() {
  local result="$1"
  local conclusion title summary payload

  case "${result}" in
    verified) conclusion=success; title='All signature and attestation checks verified.' ;;
    failed) conclusion=failure; title='At least one signature or attestation check failed.' ;;
    unknown) conclusion=neutral; title='At least one signature or attestation check did not reach a result.' ;;
    *) printf 'invalid verification result: %s\n' "${result}" >&2; return 1 ;;
  esac

  summary="Async verification is read-only and cannot change package tags, releases, or the completion marker. Result: ${result}."
  payload="$(jq -n \
    --arg name 'Asynchronous publication verification' \
    --arg head_sha "${GITHUB_SHA}" \
    --arg conclusion "${conclusion}" \
    --arg title "${title}" \
    --arg summary "${summary}" \
    --arg details_url "${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID}" \
    '{name: $name, head_sha: $head_sha, status: "completed", conclusion: $conclusion, details_url: $details_url, output: {title: $title, summary: $summary}}')"

  printf '%s\n' '::notice::async-verification:check-run:started' >&3
  if timeout --signal=TERM --kill-after=15s 45s \
    gh api -X POST "/repos/${GITHUB_REPOSITORY}/check-runs" --input - <<<"${payload}"; then
    printf '%s\n' '::notice::async-verification:check-run:completed' >&3
  else
    printf '%s\n' '::warning::async-verification:check-run:unknown' >&3
  fi
}

verify_publication() {
  local proof_directory="$1"
  local payload identity issuer verification_kind image result
  local -a results=()

  exec 3>&2
  export REMOTE_OPERATION_TIMEOUT="${REMOTE_OPERATION_TIMEOUT:-2m}"
  export REMOTE_OPERATION_KILL_GRACE="${REMOTE_OPERATION_KILL_GRACE:-30s}"

  if ! payload="$(build_completion_payload "${proof_directory}")"; then
    result=unknown
  else
    identity="$(jq -r '.signature.identity' <<<"${payload}")"
    issuer="$(jq -r '.signature.issuer' <<<"${payload}")"
    while IFS=$'\t' read -r verification_kind image; do
      results+=("$(verification_result "signature-${verification_kind}-${image##*@}" cosign verify \
        --certificate-oidc-issuer "${issuer}" \
        --certificate-identity "${identity}" "${image}")")
      if [[ "${verification_kind}" == platform ]]; then
        results+=("$(verification_result "spdx-${verification_kind}-${image##*@}" cosign verify-attestation --type spdxjson \
          --certificate-oidc-issuer "${issuer}" \
          --certificate-identity "${identity}" "${image}")")
      fi
      results+=("$(verification_result "slsa-${verification_kind}-${image##*@}" cosign verify-attestation --type slsaprovenance1 \
        --certificate-oidc-issuer "${issuer}" \
        --certificate-identity "${identity}" "${image}")")
    done < <(jq -r '.images[] | ("index\t" + .index.digest_ref), (.platforms[] | "platform\t" + .digest_ref)' <<<"${payload}")
    result="$(classify_verification_results "${results[@]}")"
  fi

  printf 'verification_result=%s\n' "${result}" >>"${GITHUB_OUTPUT}"
  printf "## Asynchronous publication verification\n\nResult: \`%s\`\n\nThis result does not change the durable completion marker.\n" "${result}" >>"${GITHUB_STEP_SUMMARY}"
  record_check "${result}"
  [[ "${result}" != failed ]]
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  [[ "$#" -eq 1 ]] || { printf 'usage: %s <proof-directory>\n' "$0" >&2; exit 2; }
  verify_publication "$1"
fi

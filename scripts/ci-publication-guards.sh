#!/usr/bin/env bash

run_remote() {
  local label="$1"
  shift
  local exit_code

  printf '%s\n' "::notice::remote-operation:${label}:started" >&3
  # This limits the direct remote operation and emits a named result. The
  # workflow's native step and job timeouts remain the outer safety boundary;
  # do not infer process-tree cleanup from this helper.
  if timeout --signal=TERM --kill-after="${REMOTE_OPERATION_KILL_GRACE}" "${REMOTE_OPERATION_TIMEOUT}" "$@"; then
    printf '%s\n' "::notice::remote-operation:${label}:completed" >&3
  else
    exit_code="$?"
    if [[ "${exit_code}" -eq 124 || "${exit_code}" -eq 137 ]]; then
      printf '%s\n' "::error::remote-operation:${label}:timed-out" >&3
    else
      printf '%s\n' "::error::remote-operation:${label}:failed" >&3
    fi
    return "${exit_code}"
  fi
}

assert_final_digest() {
  local expected_digest="$1"
  local resolved_digest="$2"

  if [[ ! "${expected_digest}" =~ ^sha256:[0-9a-f]{64}$ || ! "${resolved_digest}" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    printf '%s\n' '::error::Final tag resolution returned an invalid digest.' >&3
    return 1
  fi
  if [[ "${resolved_digest}" != "${expected_digest}" ]]; then
    printf '%s\n' "::error::Final tag resolved to ${resolved_digest}, not intended digest ${expected_digest}." >&3
    return 1
  fi
}

classify_verification_results() {
  local result
  local unknown=false

  for result in "$@"; do
    case "${result}" in
      verified) ;;
      failed) printf '%s\n' failed; return 0 ;;
      unknown) unknown=true ;;
      *) return 1 ;;
    esac
  done

  if [[ "${unknown}" == true ]]; then
    printf '%s\n' unknown
  else
    printf '%s\n' verified
  fi
}

is_manifest_unknown_error() {
  local inspection_error="$1"

  grep -Eqi '(^|[^[:alnum:]_])MANIFEST_UNKNOWN([^[:alnum:]_]|$)' "${inspection_error}"
}

#!/usr/bin/env bash

run_remote() {
  local label="$1"
  shift
  local exit_code

  printf '%s\n' "::notice::remote-operation:${label}:started" >&3
  # Do not use --foreground: it limits timeout signals to the direct command,
  # leaving command descendants outside the TERM/KILL deadline.
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

is_manifest_unknown_error() {
  local inspection_error="$1"

  grep -Eqi '(^|[^[:alnum:]_])MANIFEST_UNKNOWN([^[:alnum:]_]|$)' "${inspection_error}"
}

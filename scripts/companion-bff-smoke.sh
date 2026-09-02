#!/usr/bin/env bash
# Verify the BFF companion protocol contract end-to-end. This is a release gate:
# it fails (non-zero) when the BFF is unreachable or when any lease/event
# contract assertion diverges, so CI blocks a tag before assets are attached.
#
# Required environment:
#   COMPANION_BFF_URL    Base URL of the BFF (e.g. https://bff.example.com)
#   COMPANION_ID         Companion identifier (matches a provisioned key)
#   COMPANION_KEY_ID     Companion signing key id
#   COMPANION_SECRET     Companion signing secret (HMAC-SHA256)
#
# Requires: curl, jq, openssl, date (GNU/BSD), mktemp.
set -Eeuo pipefail

: "${COMPANION_BFF_URL:?COMPANION_BFF_URL is required}"
: "${COMPANION_ID:?COMPANION_ID is required}"
: "${COMPANION_KEY_ID:?COMPANION_KEY_ID is required}"
: "${COMPANION_SECRET:?COMPANION_SECRET is required}"

bff="${COMPANION_BFF_URL%/}"
lease_path="/companion/v1/import-jobs/lease"

body_sha256() { printf '%s' "$1" | openssl dgst -sha256 -hex | awk '{print $2}'; }

canonical_string() {
  # method, path, body_sha256, timestamp, nonce -> seven newline-joined lines
  printf '%s\n%s\n%s\n%s\n%s\n%s\n%s' \
    "$1" "$2" "$3" "$COMPANION_ID" "$COMPANION_KEY_ID" "$4" "$5"
}

sign() { printf '%s' "$1" | openssl dgst -sha256 -hmac "$2" -binary | openssl base64 -A; }

post() {
  # method path body [tamper] -> "STATUS|BODY"
  local method="$1" path="$2" body="$3" tamper="${4:-0}"
  local sha ts nonce canonical sig tmp code json
  sha="$(body_sha256 "$body")"
  ts="$(date -u +%s)"
  nonce="$(openssl rand -hex 16)"
  canonical="$(canonical_string "$method" "$path" "$sha" "$ts" "$nonce")"
  sig="$(sign "$canonical" "$COMPANION_SECRET")"
  if [[ "$tamper" == "1" ]]; then
    sig="$(sign "$canonical" "tampered-$COMPANION_SECRET")"
  fi
  tmp="$(mktemp)"
  if ! code="$(curl -sS -o "$tmp" -w '%{http_code}' \
    -X "$method" \
    -H "Content-Type: application/json" \
    -H "X-Companion-Id: $COMPANION_ID" \
    -H "X-Companion-Key-Id: $COMPANION_KEY_ID" \
    -H "X-Companion-Timestamp: $ts" \
    -H "X-Companion-Nonce: $nonce" \
    -H "X-Companion-Body-SHA256: $sha" \
    -H "X-Companion-Signature: $sig" \
    --data-binary "$body" \
    "$bff$path")"; then
    rm -f "$tmp"
    printf '0|unreachable'
    return
  fi
  json="$(cat "$tmp")"
  rm -f "$tmp"
  printf '%s|%s' "$code" "$json"
}

fail() { echo "::error::$1"; exit 1; }

# 1. Lease acquisition: 200 (lease) or 204 (no work) are both valid.
lease_resp="$(post POST "$lease_path" '{"protocolVersion":1}')"
lease_code="${lease_resp%%|*}"
lease_json="${lease_resp#*|}"

if [[ "$lease_code" == "204" ]]; then
  echo "No import work available (204); companion BFF smoke passes."
  exit 0
fi
[[ "$lease_code" == "200" ]] || fail "Lease returned $lease_code (expected 200 or 204)."

job_id="$(printf '%s' "$lease_json" | jq -r '.jobId')"
lease_id="$(printf '%s' "$lease_json" | jq -r '.leaseId')"
next_seq="$(printf '%s' "$lease_json" | jq -r '.nextSequence')"
[[ -n "$job_id" && "$job_id" != "null" ]] || fail "Lease response is missing jobId."
[[ -n "$lease_id" && "$lease_id" != "null" ]] || fail "Lease response is missing leaseId."
[[ "$next_seq" =~ ^[0-9]+$ ]] || fail "Lease response has an invalid nextSequence."

events_path="/companion/v1/import-jobs/${job_id}/events"

event_body() {
  # state sequence event_id processed failed
  jq -nc --argjson pv 1 --arg leaseId "$lease_id" --arg eventId "$3" \
    --argjson sequence "$2" --arg state "$1" \
    --argjson processed "$4" --argjson failed "$5" \
    '{protocolVersion:$pv, leaseId:$leaseId, eventId:$eventId, sequence:$sequence, state:$state, processed:$processed, failed:$failed}'
}

# 2. Running heartbeat at nextSequence.
running_body="$(event_body running "$next_seq" "evt-$next_seq" 0 0)"
running_resp="$(post POST "$events_path" "$running_body")"
running_code="${running_resp%%|*}"
[[ "$running_code" == "200" ]] || fail "running event returned $running_code (expected 200)."

# 3. Terminal succeeded event.
seq=$((next_seq + 1))
terminal_body="$(event_body succeeded "$seq" "evt-$seq" 1 0)"
terminal_resp="$(post POST "$events_path" "$terminal_body")"
terminal_code="${terminal_resp%%|*}"
terminal_json="${terminal_resp#*|}"
[[ "$terminal_code" == "200" ]] || fail "terminal event returned $terminal_code (expected 200)."

# 4. Exact duplicate terminal event must replay (replayed:true), not advance.
dup_resp="$(post POST "$events_path" "$terminal_body")"
dup_code="${dup_resp%%|*}"
dup_json="${dup_resp#*|}"
[[ "$dup_code" == "200" ]] || fail "duplicate terminal event returned $dup_code (expected 200)."
[[ "$(printf '%s' "$dup_json" | jq -r '.replayed')" == "true" ]] \
  || fail "duplicate terminal event did not report replayed:true."

# 5. Regressed/mutated sequence must be rejected with 409.
regressed_body="$(event_body running "$seq" "evt-$seq" 1 0)"
regressed_resp="$(post POST "$events_path" "$regressed_body")"
regressed_code="${regressed_resp%%|*}"
[[ "$regressed_code" == "409" ]] || fail "regressed sequence returned $regressed_code (expected 409)."

# 6. Bad HMAC signature must be rejected with 401.
bad_resp="$(post POST "$events_path" "$running_body" 1)"
bad_code="${bad_resp%%|*}"
[[ "$bad_code" == "401" ]] || fail "bad HMAC returned $bad_code (expected 401)."

echo "Companion BFF contract verified: lease, running, terminal, replay, regressed 409, bad HMAC 401."

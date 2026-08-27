#!/usr/bin/env bash

set -Eeuo pipefail

repo_root="$(git rev-parse --show-toplevel)"
# shellcheck disable=SC1090
source "${repo_root}/scripts/ci-multiarch-index.sh"

fail() {
  printf 'test failure: %s\n' "$*" >&2
  exit 1
}

amd64_digest="sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
arm64_digest="sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
valid_manifest="$(jq -n --arg amd64 "${amd64_digest}" --arg arm64 "${arm64_digest}" '
  {manifests: [
    {digest: $amd64, platform: {os: "linux", architecture: "amd64"}},
    {digest: $arm64, platform: {os: "linux", architecture: "arm64"}}
  ]}
')"

assert_multiarch_manifest "${valid_manifest}" "${amd64_digest}" "${arm64_digest}"

if assert_multiarch_manifest "${valid_manifest}" "${arm64_digest}" "${amd64_digest}" >/dev/null 2>&1; then
  fail 'manifest accepted platform digests in the wrong architecture slots'
fi

single_platform_manifest="$(jq -n --arg amd64 "${amd64_digest}" '{manifests: [{digest: $amd64, platform: {os: "linux", architecture: "amd64"}}]}')"
if assert_multiarch_manifest "${single_platform_manifest}" "${amd64_digest}" "${arm64_digest}" >/dev/null 2>&1; then
  fail 'manifest without linux/arm64 was accepted'
fi

printf '%s\n' 'ci multi-architecture index tests passed'

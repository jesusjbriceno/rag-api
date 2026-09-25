#!/usr/bin/env bash

set -Eeuo pipefail

repo_root="$(git rev-parse --show-toplevel)"
# shellcheck disable=SC1090
source "${repo_root}/scripts/ci-publication-guards.sh"

fail() {
  printf 'multi-architecture publication error: %s\n' "$*" >&2
  return 1
}

assert_multiarch_manifest() {
  local manifest="$1"
  local amd64_digest="$2"
  local arm64_digest="$3"

  if jq -e \
    --arg amd64_digest "${amd64_digest}" \
    --arg arm64_digest "${arm64_digest}" '
      .manifests | type == "array"
      and length == 2
      and ([.[] | select(.platform.os == "linux" and .platform.architecture == "amd64") | .digest] == [$amd64_digest])
      and ([.[] | select(.platform.os == "linux" and .platform.architecture == "arm64") | .digest] == [$arm64_digest])
    ' <<<"${manifest}" >/dev/null; then
    return 0
  fi
  printf '%s\n' 'multi-architecture publication error: multi-platform index must contain exactly the expected linux/amd64 and linux/arm64 manifests' >&2
  return 1
}

resolve_platform_digest() {
  local image="$1"
  local publication_tag="$2"
  local architecture="$3"
  local digest

  digest="$(run_remote "ghcr-resolve-${architecture}-tag" crane digest "${image}:${publication_tag}-${architecture}")"
  [[ "${digest}" =~ ^sha256:[0-9a-f]{64}$ ]] || fail "${architecture} tag resolved to an invalid digest"
  printf '%s\n' "${digest}"
}

assert_platform_source_revision() {
  local image="$1"
  local digest="$2"
  local configuration

  configuration="$(run_remote 'ghcr-read-platform-config' crane config "${image}@${digest}")"
  jq -e --arg source_revision "${GITHUB_SHA}" '
    .config.Labels["org.opencontainers.image.revision"] == $source_revision
  ' <<<"${configuration}" >/dev/null || fail "platform manifest ${digest} was not built from ${GITHUB_SHA}"
}

assert_index_membership() {
  local image="$1"
  local index_digest="$2"
  local amd64_digest="$3"
  local arm64_digest="$4"
  local manifest

  manifest="$(run_remote 'ghcr-read-multiarch-index' crane manifest "${image}@${index_digest}")"
  assert_multiarch_manifest "${manifest}" "${amd64_digest}" "${arm64_digest}"
}

create_or_reuse_staging_index() {
  local image="$1"
  local publication_tag="$2"
  local amd64_digest="$3"
  local arm64_digest="$4"
  local staging_tag="staging-${publication_tag}"
  local inspection_error index_digest

  inspection_error="$(mktemp)"
  if index_digest="$(run_remote 'ghcr-inspect-staging-index' crane digest "${image}:${staging_tag}" 2>"${inspection_error}")"; then
    :
  else
    local inspection_exit_code="$?"
    if ! is_manifest_unknown_error "${inspection_error}"; then
      rm -f "${inspection_error}"
      printf '%s\n' '::error::remote-operation:ghcr-inspect-staging-index:unresolved' >&3
      return "${inspection_exit_code}"
    fi
    rm -f "${inspection_error}"
    run_remote 'ghcr-create-staging-index' docker buildx imagetools create \
      --tag "${image}:${staging_tag}" \
      "${image}@${amd64_digest}" \
      "${image}@${arm64_digest}" >/dev/null
    index_digest="$(run_remote 'ghcr-resolve-staging-index' crane digest "${image}:${staging_tag}")"
  fi

  [[ "${index_digest}" =~ ^sha256:[0-9a-f]{64}$ ]] || fail 'staging multi-platform index resolved to an invalid digest'
  assert_index_membership "${image}" "${index_digest}" "${amd64_digest}" "${arm64_digest}"
  printf '%s\n' "${index_digest}"
}

promote_multiarch_index() {
  local component="$1"
  local image="$2"
  local publication_tag="$3"
  local proof_output="$4"
  local identity="$5"
  local issuer="$6"
  local amd64_digest arm64_digest index_digest final_tag_digest inspection_error provenance

  amd64_digest="$(resolve_platform_digest "${image}" "${publication_tag}" amd64)"
  arm64_digest="$(resolve_platform_digest "${image}" "${publication_tag}" arm64)"
  assert_platform_source_revision "${image}" "${amd64_digest}"
  assert_platform_source_revision "${image}" "${arm64_digest}"
  index_digest="$(create_or_reuse_staging_index "${image}" "${publication_tag}" "${amd64_digest}" "${arm64_digest}")"

  provenance="${RUNNER_TEMP}/multiarch-provenance-${component}.json"
  jq -n \
    --arg source_revision "${GITHUB_SHA}" \
    --arg build_type "${identity}" \
    --arg source_uri "git+https://github.com/${GITHUB_REPOSITORY}@${GITHUB_REF}" \
    --arg invocation_id "${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID}" \
    --arg amd64_digest "${amd64_digest}" \
    --arg arm64_digest "${arm64_digest}" '
      {
        buildDefinition: {
          buildType: $build_type,
          externalParameters: {
            source: {uri: $source_uri, digest: {sha1: $source_revision}},
            platforms: {
              "linux/amd64": {digest: $amd64_digest},
              "linux/arm64": {digest: $arm64_digest}
            }
          }
        },
        runDetails: {
          builder: {id: $build_type},
          metadata: {invocationId: $invocation_id}
        }
      }
    ' >"${provenance}"

  run_remote 'cosign-sign-multiarch-index' cosign sign --yes "${image}@${index_digest}"
  run_remote 'cosign-attest-multiarch-slsa' cosign attest --yes \
    --type slsaprovenance1 \
    --predicate "${provenance}" \
    "${image}@${index_digest}"

  inspection_error="$(mktemp)"
  if final_tag_digest="$(run_remote 'ghcr-inspect-final-multiarch-tag' crane digest "${image}:${publication_tag}" 2>"${inspection_error}")"; then
    rm -f "${inspection_error}"
    assert_final_digest "${index_digest}" "${final_tag_digest}"
  else
    local inspection_exit_code="$?"
    if ! is_manifest_unknown_error "${inspection_error}"; then
      rm -f "${inspection_error}"
      printf '%s\n' '::error::remote-operation:ghcr-inspect-final-multiarch-tag:unresolved' >&3
      return "${inspection_exit_code}"
    fi
    rm -f "${inspection_error}"
    run_remote 'cosign-copy-final-multiarch-tag' cosign copy "${image}@${index_digest}" "${image}:${publication_tag}"
    final_tag_digest="$(run_remote 'ghcr-resolve-final-multiarch-tag' crane digest "${image}:${publication_tag}")"
    assert_final_digest "${index_digest}" "${final_tag_digest}"
  fi
  assert_index_membership "${image}" "${final_tag_digest}" "${amd64_digest}" "${arm64_digest}"

  jq -n \
    --arg component "${component}" \
    --arg source_revision "${GITHUB_SHA}" \
    --arg run_id "${GITHUB_RUN_ID}" \
    --arg run_url "${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID}" \
    --arg publication_tag "${publication_tag}" \
    --arg identity "${identity}" \
    --arg issuer "${issuer}" \
    --arg image "${image}" \
    --arg index_digest "${final_tag_digest}" \
    --arg amd64_digest "${amd64_digest}" \
    --arg arm64_digest "${arm64_digest}" '
      {
        component: $component,
        source_revision: $source_revision,
        workflow: {run_id: $run_id, url: $run_url},
        publication_tag: $publication_tag,
        signature: {identity: $identity, issuer: $issuer},
        index: {
          digest_ref: ($image + "@" + $index_digest),
          final_tag_ref: ($image + ":" + $publication_tag),
          final_tag_digest: $index_digest,
          attachments: {signature: "attached", slsa: "attached"}
        },
        platforms: [
          {
            architecture: "amd64",
            digest_ref: ($image + "@" + $amd64_digest),
            tag_ref: ($image + ":" + $publication_tag + "-amd64"),
            tag_digest: $amd64_digest,
            attachments: {signature: "attached", spdx: "attached", slsa: "attached"}
          },
          {
            architecture: "arm64",
            digest_ref: ($image + "@" + $arm64_digest),
            tag_ref: ($image + ":" + $publication_tag + "-arm64"),
            tag_digest: $arm64_digest,
            attachments: {signature: "attached", spdx: "attached", slsa: "attached"}
          }
        ]
      }
    ' >"${proof_output}"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  [[ "$#" -eq 6 ]] || { printf 'usage: %s <component> <image> <publication-tag> <proof-output> <identity> <issuer>\n' "$0" >&2; exit 2; }
  exec 3>&2
  export REMOTE_OPERATION_TIMEOUT="${REMOTE_OPERATION_TIMEOUT:-5m}"
  export REMOTE_OPERATION_KILL_GRACE="${REMOTE_OPERATION_KILL_GRACE:-30s}"
  promote_multiarch_index "$@"
fi

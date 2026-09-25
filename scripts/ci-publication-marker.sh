#!/usr/bin/env bash

set -Eeuo pipefail

fail() {
  printf 'publication marker error: %s\n' "$*" >&2
  return 1
}

read_payload() {
  local payload="$1"
  jq -ce '
    type == "object"
    and .schema_version == 2
    and (.source_revision | type == "string" and test("^[0-9a-f]{40}$"))
    and (.workflow | type == "object"
      and (.run_id | type == "string" and test("^[0-9]+$"))
      and (.url | type == "string" and startswith("https://")))
    and (.publication_tag | type == "string" and length > 0)
    and (.signature | type == "object"
      and (.identity | type == "string" and startswith("https://github.com/"))
      and .issuer == "https://token.actions.githubusercontent.com")
    and (.images | type == "array" and length == 2
      and ([.[].component] | sort) == ["api", "operator"]
      and all(.[];
        (.index | type == "object"
          and (.digest_ref | type == "string" and test("@sha256:[0-9a-f]{64}$"))
          and (.final_tag_ref | type == "string" and contains(":"))
          and (.final_tag_digest | type == "string" and test("^sha256:[0-9a-f]{64}$"))
          and (. as $index | ($index.digest_ref | split("@")[1]) == $index.final_tag_digest)
          and .attachments == {"signature":"attached", "slsa":"attached"})
        and (.platforms | type == "array" and length == 2
          and ([.[].architecture] | sort) == ["amd64", "arm64"]
          and all(.[];
            (.digest_ref | type == "string" and test("@sha256:[0-9a-f]{64}$"))
            and (.tag_ref | type == "string" and contains(":"))
            and (.tag_digest | type == "string" and test("^sha256:[0-9a-f]{64}$"))
            and (. as $platform | ($platform.digest_ref | split("@")[1]) == $platform.tag_digest)
            and .attachments == {"signature":"attached", "spdx":"attached", "slsa":"attached"}))))
  ' <<<"${payload}" >/dev/null
}

build_completion_payload() {
  local proof_directory="$1"
  local api_proof="${proof_directory}/api.json"
  local operator_proof="${proof_directory}/operator.json"
  local payload

  [[ -f "${api_proof}" && -f "${operator_proof}" ]] || fail 'both API and operator publication proofs are required'
  payload="$(jq -ncS --slurpfile api "${api_proof}" --slurpfile operator "${operator_proof}" '
    ($api[0]) as $api_proof
    | ($operator[0]) as $operator_proof
    | if ($api_proof.component != "api" or $operator_proof.component != "operator") then error("component proof mismatch") else . end
    | if ($api_proof.source_revision != $operator_proof.source_revision
          or $api_proof.workflow != $operator_proof.workflow
          or $api_proof.publication_tag != $operator_proof.publication_tag
          or $api_proof.signature != $operator_proof.signature) then error("proof metadata mismatch") else . end
    | {
        schema_version: 2,
        source_revision: $api_proof.source_revision,
        workflow: $api_proof.workflow,
        publication_tag: $api_proof.publication_tag,
        signature: $api_proof.signature,
        images: [$api_proof, $operator_proof]
          | map({component, index, platforms})
          | sort_by(.component)
      }
  ')"
  read_payload "${payload}" || fail 'publication proofs do not satisfy the completion-marker contract'
  printf '%s\n' "${payload}"
}

marker_payload() {
  local deployment="$1"
  jq -ce '.payload | if type == "string" then fromjson else . end' <<<"${deployment}"
}

record_completion_marker() {
  local proof_directory="$1"
  local repository="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
  local expected_payload deployments existing_count existing_payload existing_id statuses request created marker_id

  expected_payload="$(build_completion_payload "${proof_directory}")"
  deployments="$(gh api --paginate "/repos/${repository}/deployments?sha=${GITHUB_SHA}&environment=publication-completion&task=publication-completion&per_page=100" | jq -sc 'add')"
  existing_count="$(jq --argjson expected "${expected_payload}" '
    [ .[] | select((.payload | if type == "string" then fromjson else . end | del(.workflow)) == ($expected | del(.workflow))) ] | length
  ' <<<"${deployments}")"

  if [[ "${existing_count}" -gt 1 ]]; then
    fail 'multiple completion markers match the same source and images'
  fi
  if [[ "${existing_count}" -eq 1 ]]; then
    existing_payload="$(jq -c --argjson expected "${expected_payload}" '
      .[] | select((.payload | if type == "string" then fromjson else . end | del(.workflow)) == ($expected | del(.workflow)))
    ' <<<"${deployments}")"
    existing_id="$(jq -r '.id' <<<"${existing_payload}")"
    statuses="$(gh api --paginate "/repos/${repository}/deployments/${existing_id}/statuses?per_page=100" | jq -sc 'add')"
    jq -e 'any(.[]; .state == "success")' <<<"${statuses}" >/dev/null || fail "existing completion marker ${existing_id} is not successful"
    marker_id="${existing_id}"
    printf 'Publication completion marker %s already records this source, both multi-platform indexes, and all platform digests.\n' "${marker_id}"
  else
    request="$(jq -n --arg sha "${GITHUB_SHA}" --argjson payload "${expected_payload}" '
      {
        ref: $sha,
        task: "publication-completion",
        environment: "publication-completion",
        description: "Durable dual-image multi-architecture publication completion marker",
        auto_merge: false,
        required_contexts: [],
        transient_environment: false,
        production_environment: false,
        payload: $payload
      }
    ')"
    created="$(printf '%s' "${request}" | gh api -X POST "/repos/${repository}/deployments" --input -)"
    marker_id="$(jq -r '.id' <<<"${created}")"
    [[ "${marker_id}" =~ ^[0-9]+$ ]] || fail 'GitHub did not return a deployment marker ID'
    gh api -X POST "/repos/${repository}/deployments/${marker_id}/statuses" \
      -f state=success \
      -f environment=publication-completion \
       -f description='Both immutable multi-platform tags resolved to their intended indexes.' \
      -F auto_inactive=false >/dev/null
    printf 'Created publication completion marker %s.\n' "${marker_id}"
  fi

  printf 'marker_id=%s\n' "${marker_id}" >>"${GITHUB_OUTPUT}"
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    printf "## Publication completion marker\n\nDeployment ID: \`%s\`\n" "${marker_id}" >>"${GITHUB_STEP_SUMMARY}"
  fi
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  [[ "$#" -eq 1 ]] || { printf 'usage: %s <proof-directory>\n' "$0" >&2; exit 2; }
  record_completion_marker "$1"
fi

#!/usr/bin/env bash

set -Eeuo pipefail

repo_root="$(git rev-parse --show-toplevel)"
# shellcheck disable=SC1090
source "${repo_root}/scripts/ci-publication-marker.sh"

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

# Unit 5 AdminApp publication chain: the completion marker must cover exactly
# three images (admin, api, operator), each with two platforms, under the
# schema v3 contract with homogeneous proof metadata.
write_proof() {
  local component="$1"
  local image="$2"
  jq -n --arg component "${component}" --arg image "${image}" '
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
        digest_ref: ($image + "@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"),
        final_tag_ref: ($image + ":develop-0123456789abcdef0123456789abcdef01234567"),
        final_tag_digest: "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
        attachments: {signature: "attached", slsa: "attached"}
      },
      platforms: [
        {
          architecture: "amd64",
          digest_ref: ($image + "@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
          tag_ref: ($image + ":develop-0123456789abcdef0123456789abcdef01234567-amd64"),
          tag_digest: "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          attachments: {signature: "attached", spdx: "attached", slsa: "attached"}
        },
        {
          architecture: "arm64",
          digest_ref: ($image + "@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
          tag_ref: ($image + ":develop-0123456789abcdef0123456789abcdef01234567-arm64"),
          tag_digest: "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          attachments: {signature: "attached", spdx: "attached", slsa: "attached"}
        }
      ]
    }
  '
}

write_proof admin ghcr.io/jesusjbriceno/rag-adminapp >"${proof_dir}/admin.json"
write_proof api ghcr.io/example/rag-api >"${proof_dir}/api.json"
write_proof operator ghcr.io/example/rag-operator >"${proof_dir}/operator.json"

if ! payload_one="$(build_completion_payload "${proof_dir}")"; then
  fail 'marker payload must be built from admin/api/operator proofs (schema v3 publication chain)'
fi
payload_two="$(build_completion_payload "${proof_dir}")"
assert_equals "${payload_one}" "${payload_two}" 'marker payload idempotency'

jq -e '
  .schema_version == 3
  and .source_revision == "0123456789abcdef0123456789abcdef01234567"
  and .workflow.run_id == "123"
  and (.images | type == "array" and length == 3
    and ([.[].component] | sort) == ["admin", "api", "operator"]
    and ([.[].platforms[]] | length == 6)
    and all(.[];
      (.platforms | type == "array" and length == 2
        and ([.[].architecture] | sort) == ["amd64", "arm64"])
      and .index.attachments == {signature:"attached", slsa:"attached"}
      and all(.platforms[]; .attachments == {signature:"attached", spdx:"attached", slsa:"attached"})))
' <<<"${payload_one}" >/dev/null || fail 'marker payload content is incomplete for the three-image schema v3 contract'

# Homogeneous metadata: all three proofs must share the same source revision,
# workflow, publication tag, and signature identity.
heterogeneous_dir="${temp_dir}/heterogeneous"
mkdir -p "${heterogeneous_dir}"
write_proof admin ghcr.io/jesusjbriceno/rag-adminapp >"${heterogeneous_dir}/admin.json"
write_proof api ghcr.io/example/rag-api >"${heterogeneous_dir}/api.json"
write_proof operator ghcr.io/example/rag-operator >"${heterogeneous_dir}/operator.json"
jq '.workflow.run_id = "456"' "${heterogeneous_dir}/admin.json" >"${heterogeneous_dir}/admin-heterogeneous.json"
mv "${heterogeneous_dir}/admin-heterogeneous.json" "${heterogeneous_dir}/admin.json"
if (build_completion_payload "${heterogeneous_dir}" >/dev/null 2>&1); then
  fail 'marker payload accepted heterogeneous workflow metadata across proofs'
fi

# A platform tag digest that does not match its image digest must be rejected.
mismatch_dir="${temp_dir}/mismatch"
mkdir -p "${mismatch_dir}"
write_proof admin ghcr.io/jesusjbriceno/rag-adminapp >"${mismatch_dir}/admin.json"
write_proof api ghcr.io/example/rag-api >"${mismatch_dir}/api.json"
write_proof operator ghcr.io/example/rag-operator >"${mismatch_dir}/operator.json"
jq '.platforms[1].tag_digest = "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"' \
  "${mismatch_dir}/operator.json" >"${mismatch_dir}/operator-invalid.json"
mv "${mismatch_dir}/operator-invalid.json" "${mismatch_dir}/operator.json"
if (build_completion_payload "${mismatch_dir}" >/dev/null 2>&1); then
  fail 'marker payload accepted a platform tag digest different from its image digest'
fi

# Missing admin.json must fail: the publication chain covers three images.
missing_admin_dir="${temp_dir}/missing-admin"
mkdir -p "${missing_admin_dir}"
write_proof api ghcr.io/example/rag-api >"${missing_admin_dir}/api.json"
write_proof operator ghcr.io/example/rag-operator >"${missing_admin_dir}/operator.json"
if (build_completion_payload "${missing_admin_dir}" >/dev/null 2>&1); then
  fail 'marker payload was created without the admin publication proof'
fi

existing_deployment="$(jq -nc --argjson payload "${payload_one}" '{id: 42, payload: $payload}')"
api_calls="${temp_dir}/api-calls"
gh() {
  printf '%s\n' "$*" >>"${api_calls}"
  case "$*" in
    *'/deployments?sha='*) jq -nc --argjson deployment "${existing_deployment}" '[$deployment]' ;;
    *'/deployments/42/statuses?'*) printf '%s\n' '[{"state":"success"}]' ;;
    *) fail "unexpected gh api call: $*" ;;
  esac
}
GITHUB_REPOSITORY='example/repo'
GITHUB_SHA='0123456789abcdef0123456789abcdef01234567'
GITHUB_OUTPUT="${temp_dir}/github-output"
record_completion_marker "${proof_dir}" >/dev/null
assert_equals 'marker_id=42' "$(<"${GITHUB_OUTPUT}")" 'existing marker ID'
if grep -q -- '-X POST' "${api_calls}"; then
  fail 'idempotent marker reuse created a deployment'
fi
unset -f gh

printf '%s\n' 'ci publication marker tests passed'

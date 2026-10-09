#!/usr/bin/env bash

set -Eeuo pipefail

# Retention for GHCR container package versions, shared by ci-release and
# ci-develop so the destructive selector exists in exactly one place.
#
# Class rules (a version is prunable only if it belongs to exactly one
# retention class and carries no tag outside that class's allowed prefixes):
#
#   staging class (used by ci-release): the version has at least one tag,
#   and EVERY tag starts with "staging-".
#
#   develop class (used by ci-develop): the version has at least one tag
#   starting with "develop-", and NO tag starts with anything outside
#   "develop-", "sha256-" and "staging-". The develop multi-platform index
#   carries "develop-<sha>", "sha256-<digest>" and "staging-develop-<sha>"
#   on the same version, so requiring every tag to start with "develop-"
#   would match nothing, and requiring any tag to start with "develop-"
#   alone is what let the previous inline selector delete a published
#   release.
#
# ABSOLUTE RULE, enforced by construction in both the jq selector and the
# defensive re-check below: a version carrying any tag that looks like a
# release tag ("v" followed by a digit) is NEVER prunable, in any class,
# at any age.
#
# Why the rule is this strict: promotion applies the final release tag to
# the same manifest that was first published under "staging-<release-tag>",
# so in GHCR ONE version carries BOTH tags (for example "v0.1.0" and
# "staging-v0.1.0"). The GHCR API cannot remove a single tag from a
# version; deleting the version would delete the release tag with it and
# strand every deployment pinned to that image. Untagging is therefore not
# an option, and the only non-destructive behaviour is to keep versions
# that carry a release tag and prune only versions whose tags are
# exclusively within one retention class.

readonly STAGING_DEFAULT_MIN_AGE_SECONDS=86400
readonly DEVELOP_MIN_AGE_SECONDS=2592000 # 30 days
readonly DEVELOP_RETAIN_NEWEST=20

usage() {
  printf 'usage: %s select [staging|develop] < versions.json\n' "$0" >&2
  printf '       %s prune <owner> <package> <min-age-seconds>          # staging class\n' "$0" >&2
  printf '       %s prune-develop <owner> <package>                    # develop class\n' "$0" >&2
  exit 2
}

# Emits one prunable version id per line from a GHCR versions JSON array
# on stdin. The selection performs no network access.
#
# Pipeline per class: select class members, order newest first, drop the
# newest ${retain} versions (retain-N window, no-op for staging), then keep
# only those older than the threshold. The explicit release-tag filter
# makes the absolute rule hold by construction even if a class prefix rule
# were ever relaxed.
select_prunable_ids() {
  local class="$1"
  local min_age_seconds="$2"
  local retain_newest="$3"

  case "${class}" in
    staging)
      jq -r --argjson min_age "${min_age_seconds}" --argjson retain "${retain_newest}" '
        [ .[]?
          | select((.metadata.container.tags // []) | length > 0)
          | select((.metadata.container.tags // []) | all(startswith("staging-")))
          | select((.metadata.container.tags // []) | all(test("^v[0-9]") | not))
        ]
        | sort_by(.created_at) | reverse
        | .[$retain:]
        | sort_by(.created_at)
        | .[]
        | select(((now - (.created_at | fromdateiso8601)) > $min_age))
        | .id
      '
      ;;
    develop)
      jq -r --argjson min_age "${min_age_seconds}" --argjson retain "${retain_newest}" '
        [ .[]?
          | select((.metadata.container.tags // []) | length > 0)
          | select((.metadata.container.tags // []) | any(startswith("develop-")))
          | select((.metadata.container.tags // []) | all(test("^(develop-|sha256-|staging-)")))
          | select((.metadata.container.tags // []) | all(test("^v[0-9]") | not))
        ]
        | sort_by(.created_at) | reverse
        | .[$retain:]
        | sort_by(.created_at)
        | .[]
        | select(((now - (.created_at | fromdateiso8601)) > $min_age))
        | .id
      '
      ;;
    *)
      printf 'unknown retention class: %s\n' "${class}" >&2
      return 2
      ;;
  esac
}

# Defensive re-check before every delete: returns 0 only when the given
# tag list satisfies the class rule, including the absolute release-tag
# rule. Never delete a version that does not re-verify here; log the skip
# so the decision is visible in the run log.
version_is_prunable() {
  local class="$1"
  shift
  local tag
  local has_class_tag=false

  [[ "$#" -gt 0 ]] || return 1

  # Absolute rule: a release-looking tag is never prunable, in any class.
  for tag in "$@"; do
    if [[ "${tag}" =~ ^v[0-9] ]]; then
      return 1
    fi
  done

  case "${class}" in
    staging)
      for tag in "$@"; do
        [[ "${tag}" == staging-* ]] || return 1
      done
      ;;
    develop)
      for tag in "$@"; do
        case "${tag}" in
          develop-*)
            has_class_tag=true
            ;;
          sha256-*|staging-*)
            ;;
          *)
            return 1
            ;;
        esac
      done
      [[ "${has_class_tag}" == true ]]
      ;;
    *)
      return 1
      ;;
  esac
}

prune_package() {
  local class="$1"
  local owner="$2"
  local package="$3"
  local min_age_seconds="$4"
  local retain_newest="$5"
  local versions selected tags version_id

  versions="$(gh api --paginate "/users/${owner}/packages/container/${package}/versions")"
  selected="$(select_prunable_ids "${class}" "${min_age_seconds}" "${retain_newest}" <<<"${versions}")"

  if [[ -z "${selected}" ]]; then
    echo "No stale ${class}-class versions to prune for ${package}."
    return 0
  fi

  while IFS= read -r version_id; do
    [[ -n "${version_id}" ]] || continue

    tags="$(jq -r --argjson id "${version_id}" \
      '.[] | select(.id == $id) | (.metadata.container.tags // []) | join(" ")' \
      <<<"${versions}")"

    if ! version_is_prunable "${class}" ${tags}; then
      echo "Skipped version ${version_id} of ${package}: its tags do not satisfy the ${class}-class rule: ${tags}"
      continue
    fi

    gh api -X DELETE "/users/${owner}/packages/container/${package}/versions/${version_id}" >/dev/null
    echo "Deleted stale ${class} version ${version_id} of ${package} (tags: ${tags})"
  done <<<"${selected}"
}

main() {
  case "${1:-}" in
    select)
      case "${2:-staging}" in
        staging)
          [[ "$#" -le 2 ]] || usage
          select_prunable_ids staging "${STAGING_DEFAULT_MIN_AGE_SECONDS}" 0
          ;;
        develop)
          [[ "$#" -le 2 ]] || usage
          select_prunable_ids develop "${DEVELOP_MIN_AGE_SECONDS}" "${DEVELOP_RETAIN_NEWEST}"
          ;;
        *)
          usage
          ;;
      esac
      ;;
    prune)
      [[ "$#" -eq 4 ]] || usage
      prune_package staging "$2" "$3" "$4" 0
      ;;
    prune-develop)
      [[ "$#" -eq 3 ]] || usage
      prune_package develop "$2" "$3" "${DEVELOP_MIN_AGE_SECONDS}" "${DEVELOP_RETAIN_NEWEST}"
      ;;
    *)
      usage
      ;;
  esac
}

main "$@"

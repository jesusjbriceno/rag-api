#!/usr/bin/env bash

set -Eeuo pipefail

# Staging version retention for GHCR container packages.
#
# Selection rule: a package version is prunable if and only if
#   (a) it has at least one tag,
#   (b) EVERY one of its tags starts with "staging-", and
#   (c) it is older than the minimum age in seconds.
# A version carrying any tag that does not start with "staging-" is never
# prunable, regardless of age.
#
# Why the rule is this strict: promotion applies the final release tag to
# the same manifest that was first published under "staging-<release-tag>",
# so in GHCR ONE version carries BOTH tags (for example "v0.1.0" and
# "staging-v0.1.0"). The GHCR API cannot remove a single tag from a
# version; deleting the version would delete the release tag with it and
# strand every deployment pinned to that image. Untagging is therefore not
# an option, and the only non-destructive behaviour is to keep versions
# that carry a release tag and prune only versions whose tags are
# exclusively staging tags.

usage() {
  printf 'usage: %s select < versions.json\n' "$0" >&2
  printf '       %s prune <owner> <package> <min-age-seconds>\n' "$0" >&2
  exit 2
}

# Emits one prunable version id per line from a GHCR versions JSON array
# on stdin. The selection performs no network access.
select_prunable_ids() {
  local min_age_seconds="$1"

  jq -r --argjson min_age "${min_age_seconds}" '
    .[]?
    | select((.metadata.container.tags // []) | length > 0)
    | select((.metadata.container.tags // []) | all(startswith("staging-")))
    | select(((now - (.created_at | fromdateiso8601)) > $min_age))
    | .id
  '
}

prune_package() {
  local owner="$1"
  local package="$2"
  local min_age_seconds="$3"
  local versions selected tags tag version_id has_non_staging_tag

  versions="$(gh api --paginate "/users/${owner}/packages/container/${package}/versions")"
  selected="$(select_prunable_ids "${min_age_seconds}" <<<"${versions}")"

  if [[ -z "${selected}" ]]; then
    echo "No stale staging-only versions to prune for ${package}."
    return 0
  fi

  while IFS= read -r version_id; do
    [[ -n "${version_id}" ]] || continue

    # Defensive re-check: the selector already excludes versions with any
    # non-staging tag, but never delete a version whose tag list contains
    # a non-staging tag without re-verifying it here. Log the skip so the
    # decision is visible in the run log.
    tags="$(jq -r --argjson id "${version_id}" \
      '.[] | select(.id == $id) | (.metadata.container.tags // []) | join(" ")' \
      <<<"${versions}")"
    has_non_staging_tag=false
    for tag in ${tags}; do
      if [[ "${tag}" != staging-* ]]; then
        has_non_staging_tag=true
        break
      fi
    done
    if [[ "${has_non_staging_tag}" == true ]]; then
      echo "Skipped version ${version_id} of ${package}: it carries non-staging tag(s): ${tags}"
      continue
    fi

    gh api -X DELETE "/users/${owner}/packages/container/${package}/versions/${version_id}" >/dev/null
    echo "Deleted stale staging version ${version_id} of ${package} (tags: ${tags})"
  done <<<"${selected}"
}

main() {
  if [[ "${1:-}" == "select" ]]; then
    [[ "$#" -eq 1 ]] || usage
    select_prunable_ids 86400
  elif [[ "${1:-}" == "prune" ]]; then
    [[ "$#" -eq 4 ]] || usage
    prune_package "$2" "$3" "$4"
  else
    usage
  fi
}

main "$@"

#!/usr/bin/env bash

set -Eeuo pipefail

repo_root="$(git rev-parse --show-toplevel)"
workflows_dir="${repo_root}/.github/workflows"
admin_image="ghcr.io/jesusjbriceno/rag-adminapp"

fail() {
  printf 'test failure: %s\n' "$*" >&2
  exit 1
}

require_file() {
  [[ -f "$1" ]] || fail "missing workflow file: $1"
}

# Extract the include entries of a job matrix as single lines, for example:
#   component: api|target: api|image: ghcr.io/example/rag-api|architecture: amd64|runner: ubuntu-latest
extract_matrix_entries() {
  local file="$1" job="$2"
  awk -v job="${job}" '
    BEGIN { injob = 0; inmatrix = 0; ininclude = 0; inentry = 0; entry = "" }
    $0 ~ ("^  " job ":") { injob = 1; inmatrix = 0; ininclude = 0; next }
    injob && /^  [A-Za-z_][A-Za-z0-9_-]*:/ && $0 !~ ("^  " job ":") {
      if (inentry) { print entry; inentry = 0 }
      injob = 0; inmatrix = 0; ininclude = 0
    }
    injob && /^      matrix:/ { inmatrix = 1; next }
    inmatrix && /^        include:/ { ininclude = 1; inentry = 0; entry = ""; next }
    ininclude && /^          - / {
      if (inentry) print entry
      inentry = 1
      entry = substr($0, 13)
      next
    }
    ininclude && inentry && /^            [A-Za-z_]/ {
      entry = entry "|" substr($0, 13)
      next
    }
    ininclude && inentry {
      print entry
      inentry = 0
    }
    END { if (inentry) print entry }
  ' "${file}"
}

entry_field() {
  local entry="$1" field="$2" pair
  while IFS= read -r pair; do
    if [[ "${pair}" == "${field}: "* ]]; then
      printf '%s' "${pair#"${field}": }"
      return 0
    fi
  done < <(tr '|' '\n' <<<"${entry}")
  return 1
}

# The package job must build, scan, sign, and publish the admin image on both
# native platforms, with the admin Docker target and a pinned native runner.
assert_admin_package_rows() {
  local file="$1" workflow="$2" entries entry component architecture target image runner
  local amd64_found=0 arm64_found=0
  entries="$(extract_matrix_entries "${file}" package)"
  [[ -n "${entries}" ]] || fail "${workflow}: package job matrix has no entries"
  while IFS= read -r entry; do
    component="$(entry_field "${entry}" component || true)"
    [[ "${component}" == "admin" ]] || continue
    architecture="$(entry_field "${entry}" architecture || true)"
    target="$(entry_field "${entry}" target || true)"
    image="$(entry_field "${entry}" image || true)"
    runner="$(entry_field "${entry}" runner || true)"
    [[ "${target}" == "admin" ]] || fail "${workflow}: admin package row must use the admin Docker target"
    [[ "${image}" == "${admin_image}" ]] || fail "${workflow}: admin package row must publish ${admin_image}"
    [[ -n "${runner}" ]] || fail "${workflow}: admin package row must pin a native runner"
    case "${architecture}" in
      amd64) amd64_found=1 ;;
      arm64) arm64_found=1 ;;
      *) fail "${workflow}: admin package row has unsupported architecture '${architecture}'" ;;
    esac
  done <<<"${entries}"
  [[ "${amd64_found}" == 1 ]] || fail "${workflow}: package matrix is missing the admin amd64 row"
  [[ "${arm64_found}" == 1 ]] || fail "${workflow}: package matrix is missing the admin arm64 row"
}

# The promotion job must assemble and publish the admin multi-platform index.
assert_admin_promotion_row() {
  local file="$1" workflow="$2" entries entry component image found=0
  entries="$(extract_matrix_entries "${file}" promotion)"
  while IFS= read -r entry; do
    component="$(entry_field "${entry}" component || true)"
    if [[ "${component}" == "admin" ]]; then
      image="$(entry_field "${entry}" image || true)"
      [[ "${image}" == "${admin_image}" ]] || fail "${workflow}: admin promotion row must publish ${admin_image}"
      found=1
    fi
  done <<<"${entries}"
  [[ "${found}" == 1 ]] || fail "${workflow}: promotion matrix is missing the admin row"
}

# Retention must prune stale admin package versions alongside api and operator.
assert_admin_retention() {
  local file="$1" workflow="$2" function
  shift 2
  for function in "$@"; do
    grep -q "${function} \"rag-adminapp\"" "${file}" \
      || fail "${workflow}: retention must prune rag-adminapp via ${function}"
  done
}

# ci-pr must wire the admin Docker target into pull-request validation.
assert_admin_pr_wiring() {
  local file="${workflows_dir}/ci-pr.yml"
  require_file "${file}"
  grep -q 'scripts/test-admin-docker-target.sh' "${file}" \
    || fail 'ci-pr must wire the admin Docker target verification into PR validation'
}

develop="${workflows_dir}/ci-develop.yml"
release="${workflows_dir}/ci-release.yml"
require_file "${develop}"
require_file "${release}"

assert_admin_package_rows "${develop}" ci-develop
assert_admin_promotion_row "${develop}" ci-develop
assert_admin_retention "${develop}" ci-develop 'prune_develop' 'prune_staging'

assert_admin_package_rows "${release}" ci-release
assert_admin_promotion_row "${release}" ci-release
assert_admin_retention "${release}" ci-release 'prune_staging'

assert_admin_pr_wiring

printf '%s\n' 'ci admin workflow tests passed'

#!/usr/bin/env bash
set -Eeuo pipefail

readonly EXIT_USAGE=2
readonly EXIT_DATABASE=3
readonly EXIT_CONTENT=4
readonly EXIT_VERIFICATION=5

usage() {
    printf '%s\n' 'Usage: backup-rag.sh <backup-id> <output-directory> <content-directory>' >&2
    printf '%s\n' 'Requires PGHOST, PGPORT, PGDATABASE, and PGUSER. Authentication uses standard libpq configuration.' >&2
}

fail() {
    printf 'backup-rag: %s\n' "$2" >&2
    exit "$1"
}

if [[ $# -ne 3 ]]; then
    usage
    exit "$EXIT_USAGE"
fi

backup_id=$1
output_directory=$2
content_directory=$3

if [[ ! $backup_id =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
    fail "$EXIT_USAGE" 'backup id must contain only letters, digits, dots, underscores, and hyphens.'
fi

if [[ ! -d $content_directory ]]; then
    fail "$EXIT_USAGE" "content directory does not exist: $content_directory"
fi

for variable in PGHOST PGPORT PGDATABASE PGUSER; do
    if [[ -z ${!variable:-} ]]; then
        fail "$EXIT_USAGE" "$variable must be set."
    fi
done

for command in pg_dump pg_restore sha256sum tar mktemp; do
    command -v "$command" >/dev/null 2>&1 || fail "$EXIT_USAGE" "required command is unavailable: $command"
done

mkdir -p "$output_directory"
destination="$output_directory/$backup_id"
if [[ -e $destination ]]; then
    fail "$EXIT_USAGE" "backup destination already exists: $destination"
fi

stage_directory=$(mktemp -d "$output_directory/.${backup_id}.tmp.XXXXXX")
cleanup() {
    rm -rf "$stage_directory"
}
trap cleanup EXIT

umask 077
export LC_ALL=C

if ! pg_dump --no-password --format=custom --no-owner --no-privileges --file "$stage_directory/postgres.dump"; then
    fail "$EXIT_DATABASE" 'PostgreSQL dump failed.'
fi

if ! tar --create --file "$stage_directory/content.tar" --directory "$content_directory" --sort=name --numeric-owner --owner=0 --group=0 --mtime=@0 .; then
    fail "$EXIT_CONTENT" 'Content-volume snapshot failed.'
fi

{
    printf 'contract_version=1\n'
    printf 'backup_id=%s\n' "$backup_id"
    printf 'postgres_format=custom\n'
    printf 'content_format=tar\n'
    printf 'content_source=%s\n' "$content_directory"
} > "$stage_directory/manifest.txt"

if ! pg_restore --list "$stage_directory/postgres.dump" >/dev/null ||
    ! tar --list --file "$stage_directory/content.tar" >/dev/null ||
    ! sha256sum "$stage_directory/postgres.dump" "$stage_directory/content.tar" "$stage_directory/manifest.txt" > "$stage_directory/SHA256SUMS" ||
    ! (cd "$stage_directory" && sha256sum --check SHA256SUMS >/dev/null); then
    fail "$EXIT_VERIFICATION" 'Backup verification failed.'
fi

mv "$stage_directory" "$destination"
trap - EXIT
printf 'backup-rag: verified backup written to %s\n' "$destination"

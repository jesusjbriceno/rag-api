#!/bin/sh
set -eu

readonly model_directory='/models'
readonly model_file="$model_directory/Qwen3-Embedding-0.6B-Q8_0.gguf"
readonly manifest_file="$model_directory/Qwen3-Embedding-0.6B-Q8_0.manifest"
readonly source_url='https://huggingface.co/Qwen/Qwen3-Embedding-0.6B-GGUF/resolve/370f27d7550e0def9b39c1f16d3fbaa13aa67728/Qwen3-Embedding-0.6B-Q8_0.gguf'
readonly source_revision='370f27d7550e0def9b39c1f16d3fbaa13aa67728'
readonly expected_bytes='639150592'
readonly expected_sha256='06507c7b42688469c4e7298b0a1e16deff06caf291cf0a5b278c308249c3e439'

umask 077
mkdir -p "$model_directory"
temporary_model="$(mktemp "$model_directory/.Qwen3-Embedding-0.6B-Q8_0.gguf.XXXXXX")"
temporary_manifest="$(mktemp "$model_directory/.Qwen3-Embedding-0.6B-Q8_0.manifest.XXXXXX")"
trap 'rm -f "$temporary_model" "$temporary_manifest"' EXIT

curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 --output "$temporary_model" "$source_url"

actual_bytes="$(wc -c < "$temporary_model" | tr -d '[:space:]')"
if [ "$actual_bytes" != "$expected_bytes" ]; then
    printf 'Model size verification failed: expected %s bytes, got %s.\n' "$expected_bytes" "$actual_bytes" >&2
    exit 1
fi

printf '%s  %s\n' "$expected_sha256" "$temporary_model" | sha256sum -c -s

mv -f "$temporary_model" "$model_file"
cat > "$temporary_manifest" <<EOF
source_url=$source_url
source_revision=$source_revision
model_file=$(basename "$model_file")
bytes=$expected_bytes
sha256=$expected_sha256
downloaded_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
EOF
mv -f "$temporary_manifest" "$manifest_file"

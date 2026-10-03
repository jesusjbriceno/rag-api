import json
import pathlib
import re
import sys

SOURCE_URL = "https://huggingface.co/Qwen/Qwen3-Embedding-0.6B-GGUF/resolve/370f27d7550e0def9b39c1f16d3fbaa13aa67728/Qwen3-Embedding-0.6B-Q8_0.gguf"
SOURCE_REVISION = "370f27d7550e0def9b39c1f16d3fbaa13aa67728"
EXPECTED_BYTES = "639150592"
EXPECTED_SHA256 = "06507c7b42688469c4e7298b0a1e16deff06caf291cf0a5b278c308249c3e439"
DOWNLOADER_IMAGE = "curlimages/curl@sha256:94e9e444bcba979c2ea12e27ae39bee4cd10bc7041a472c4727a558e213744e6"
SERVER_IMAGE = "ghcr.io/ggml-org/llama.cpp@sha256:c005e79321f8e5731ec49a7f736aaeaac9465926c1e8f4c199c1d8a8996f26ef"
DOWNLOADER_SCRIPT_NAME = "download-llamacpp-model.sh"
SCRIPT_MOUNT_TARGET = "/scripts"

# Production application images: exact repositories and immutable references only.
API_IMAGE_REPOSITORY = "ghcr.io/jesusjbriceno/rag-api"
OPERATOR_IMAGE_REPOSITORY = "ghcr.io/jesusjbriceno/rag-operator"
APP_IMAGES = {
    "rag-api": API_IMAGE_REPOSITORY,
    "rag-migrate": OPERATOR_IMAGE_REPOSITORY,
}
# Immutable tags only: exact semver (vX.Y.Z[-prerelease]) or develop-<40-char-sha>. Never latest/floating.
IMMUTABLE_TAG_PATTERN = re.compile(r"^(v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?|develop-[0-9a-f]{40})$")
SHA256_DIGEST_PATTERN = re.compile(r"^sha256:[0-9a-f]{64}$")


def split_image_reference(reference):
    digest = None
    if "@" in reference:
        reference, digest = reference.rsplit("@", 1)
    if ":" in reference:
        repository, tag = reference.rsplit(":", 1)
    else:
        repository, tag = reference, ""
    return repository, tag, digest


def mount_target(service, target):
    for mount in service.get("volumes", []):
        if isinstance(mount, dict) and mount.get("target") == target:
            return mount
    return None


try:
    compose = json.load(sys.stdin)
except json.JSONDecodeError as error:
    raise SystemExit(f"Coolify Compose validation requires `docker compose config --format json` output on stdin: {error}.") from error
services = compose.get("services", {})
# Every service is prefixed with "rag-": Coolify attaches every container of this resource
# to the shared destination network (coollabsio/coolify#5597), where an unprefixed name
# such as "postgres" collides with Coolify's own coolify-db container and Docker DNS
# round-robins between them (coollabsio/coolify#5160). Coolify's documented mitigation is
# that "Names can be prefixed to prevent collisions".
required_services = {"rag-postgres", "rag-model-download", "rag-llama-cpp", "rag-migrate", "rag-api"}
if set(services) != required_services:
    raise SystemExit(f"Coolify Compose must contain exactly {sorted(required_services)!r}; found {sorted(services)!r}.")

if services["rag-model-download"].get("image") != DOWNLOADER_IMAGE:
    raise SystemExit("rag-model-download must use the pinned downloader image digest.")
if services["rag-model-download"].get("user") != "0:0":
    raise SystemExit("rag-model-download must run as root to atomically publish into its named volume.")
if services["rag-llama-cpp"].get("image") != SERVER_IMAGE:
    raise SystemExit("rag-llama-cpp must use the pinned server image digest.")

script_path = pathlib.Path(sys.argv[1]).resolve()
if services["rag-model-download"].get("entrypoint") != ["/bin/sh", "/scripts/download-llamacpp-model.sh"]:
    raise SystemExit("rag-model-download must execute the verified downloader script.")

download_models = mount_target(services["rag-model-download"], "/models")
runtime_models = mount_target(services["rag-llama-cpp"], "/models")
if download_models is None or download_models.get("source") != "llama-cpp-model" or download_models.get("read_only"):
    raise SystemExit("rag-model-download must have writable llama-cpp-model storage.")
if runtime_models is None or runtime_models.get("source") != "llama-cpp-model" or not runtime_models.get("read_only"):
    raise SystemExit("rag-llama-cpp must mount llama-cpp-model read-only.")

# The downloader script must be mounted as a DIRECTORY (./scripts:/scripts:ro). The first
# production deployment mounted the single file directly: Docker auto-created the missing
# bind source as a directory before the repository checkout, the mount stayed empty, and
# rag-model-download exited 0 having published nothing. A directory mount is immune to
# that ordering, and the checks below fail loudly when the script is missing, is a
# directory, or is empty.
downloader_scripts = mount_target(services["rag-model-download"], SCRIPT_MOUNT_TARGET)
if downloader_scripts is None:
    raise SystemExit("rag-model-download must bind-mount the repository scripts directory read-only at /scripts.")
downloader_scripts_source = pathlib.Path(downloader_scripts.get("source", "")).resolve()
if (
    downloader_scripts.get("type") != "bind"
    or not downloader_scripts.get("read_only")
    or downloader_scripts_source != script_path.parent
    or not downloader_scripts_source.is_dir()
):
    raise SystemExit("rag-model-download must bind-mount the repository scripts directory read-only at /scripts.")
if not script_path.exists():
    raise SystemExit("The downloader script is missing from the mounted scripts directory.")
if script_path.is_dir():
    raise SystemExit("The downloader script was auto-created as a directory; it must be a regular file.")
if script_path.stat().st_size == 0:
    raise SystemExit("The downloader script is empty.")

script = script_path.read_text(encoding="utf-8")
for required_gate in (SOURCE_URL, SOURCE_REVISION, EXPECTED_BYTES, EXPECTED_SHA256, "--proto '=https'", "sha256sum -c -s", "mv -f \"$temporary_model\" \"$model_file\"", "mv -f \"$temporary_manifest\" \"$manifest_file\""):
    if required_gate not in script:
        raise SystemExit(f"rag-model-download is missing required artifact gate: {required_gate!r}.")
if script.count("https://") != 1:
    raise SystemExit("rag-model-download must fetch only the pinned HTTPS artifact source.")

required_command = ["--model", "/models/Qwen3-Embedding-0.6B-Q8_0.gguf", "--embedding", "--pooling", "last", "--embd-normalize", "2", "--device", "none", "--offline"]
command = services["rag-llama-cpp"].get("command", [])
if any(argument not in command for argument in required_command):
    raise SystemExit("rag-llama-cpp must use the fixed CPU-only, offline embedding runtime command.")
if services["rag-llama-cpp"].get("gpus") or services["rag-llama-cpp"].get("runtime"):
    raise SystemExit("rag-llama-cpp must not request a GPU runtime.")

app_reference_kinds = {}
app_tags = {}
for name, expected_repository in APP_IMAGES.items():
    service = services[name]
    if service.get("build"):
        raise SystemExit(f"{name} must not declare a local build; production Compose is pull-only.")
    image = service.get("image")
    if not image:
        raise SystemExit(f"{name} must reference a pinned GHCR image.")
    repository, tag, digest = split_image_reference(image)
    if repository != expected_repository:
        raise SystemExit(
            f"{name} must use the {expected_repository} repository; found {repository!r} (repository drift)."
        )
    if digest is not None:
        if tag:
            raise SystemExit(f"{name} image reference must use either an immutable tag or a digest, not both.")
        if not SHA256_DIGEST_PATTERN.fullmatch(digest):
            raise SystemExit(f"{name} image digest must be sha256:<64 lowercase hex characters>.")
        app_reference_kinds[name] = "digest"
    else:
        if not tag or tag == "latest":
            raise SystemExit(f"{name} image tag must not be empty or 'latest'.")
        if not IMMUTABLE_TAG_PATTERN.fullmatch(tag):
            raise SystemExit(
                f"{name} image tag {tag!r} is not an immutable version tag (vX.Y.Z[-prerelease] or develop-<sha>)."
            )
        app_reference_kinds[name] = "tag"
        app_tags[name] = tag
    if service.get("pull_policy") != "always":
        raise SystemExit(
            f"{name} must set pull_policy: always so production pulls and never falls back to a local build."
        )

if len(set(app_reference_kinds.values())) != 1:
    raise SystemExit("rag-api and rag-migrate must both use immutable tags or both use repository-specific digests.")
if app_reference_kinds["rag-api"] == "tag" and len(set(app_tags.values())) != 1:
    raise SystemExit("rag-api and rag-migrate must use the same immutable version tag.")

api_environment = services["rag-api"].get("environment", {})
if api_environment.get("LlamaCpp__BaseUrl") != "http://rag-llama-cpp:8080/":
    raise SystemExit("rag-api must target the private rag-llama-cpp service.")

published_ports = [name for name, service in services.items() if service.get("ports")]
if published_ports:
    raise SystemExit(f"Coolify Compose must not publish ports; found {published_ports!r}.")

# Only the implicit default network is allowed. Declaring custom networks is not a valid
# isolation fix: Coolify's proxy only joins the resource-specific network, so custom
# networks cause intermittent HTTPS outages
# (https://coolify.io/docs/applications/build-packs/docker-compose, "Do Not Define Custom Networks").
networks = compose.get("networks", {})
if set(networks) != {"default"}:
    raise SystemExit(f"Coolify Compose must use only its implicit default network; found {sorted(networks)!r}.")

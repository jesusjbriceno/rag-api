import json
import pathlib
import sys

SOURCE_URL = "https://huggingface.co/Qwen/Qwen3-Embedding-0.6B-GGUF/resolve/370f27d7550e0def9b39c1f16d3fbaa13aa67728/Qwen3-Embedding-0.6B-Q8_0.gguf"
SOURCE_REVISION = "370f27d7550e0def9b39c1f16d3fbaa13aa67728"
EXPECTED_BYTES = "639150592"
EXPECTED_SHA256 = "06507c7b42688469c4e7298b0a1e16deff06caf291cf0a5b278c308249c3e439"
DOWNLOADER_IMAGE = "curlimages/curl@sha256:94e9e444bcba979c2ea12e27ae39bee4cd10bc7041a472c4727a558e213744e6"
SERVER_IMAGE = "ghcr.io/ggml-org/llama.cpp@sha256:c005e79321f8e5731ec49a7f736aaeaac9465926c1e8f4c199c1d8a8996f26ef"


def mount_target(service, target):
    for mount in service.get("volumes", []):
        if isinstance(mount, dict) and mount.get("target") == target:
            return mount
    return None


compose = json.load(sys.stdin)
services = compose.get("services", {})
required_services = {"postgres", "model-download", "llama-cpp", "migrate", "api"}
if set(services) != required_services:
    raise SystemExit(f"Coolify Compose must contain exactly {sorted(required_services)!r}; found {sorted(services)!r}.")

if services["model-download"].get("image") != DOWNLOADER_IMAGE:
    raise SystemExit("model-download must use the pinned downloader image digest.")
if services["model-download"].get("user") != "0:0":
    raise SystemExit("model-download must run as root to atomically publish into its named volume.")
if services["llama-cpp"].get("image") != SERVER_IMAGE:
    raise SystemExit("llama-cpp must use the pinned server image digest.")

script = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8")
for required_gate in (SOURCE_URL, SOURCE_REVISION, EXPECTED_BYTES, EXPECTED_SHA256, "--proto '=https'", "sha256sum -c -s", "mv -f \"$temporary_model\" \"$model_file\"", "mv -f \"$temporary_manifest\" \"$manifest_file\""):
    if required_gate not in script:
        raise SystemExit(f"model-download is missing required artifact gate: {required_gate!r}.")
if script.count("https://") != 1:
    raise SystemExit("model-download must fetch only the pinned HTTPS artifact source.")
if services["model-download"].get("entrypoint") != ["/bin/sh", "/scripts/download-llamacpp-model.sh"]:
    raise SystemExit("model-download must execute the verified downloader script.")

download_models = mount_target(services["model-download"], "/models")
runtime_models = mount_target(services["llama-cpp"], "/models")
download_script = mount_target(services["model-download"], "/scripts/download-llamacpp-model.sh")
if download_models is None or download_models.get("source") != "llama-cpp-model" or download_models.get("read_only"):
    raise SystemExit("model-download must have writable llama-cpp-model storage.")
if download_script is None or pathlib.Path(download_script.get("source", "")).resolve() != pathlib.Path(sys.argv[1]).resolve() or not download_script.get("read_only"):
    raise SystemExit("model-download must mount the downloader script read-only.")
if runtime_models is None or runtime_models.get("source") != "llama-cpp-model" or not runtime_models.get("read_only"):
    raise SystemExit("llama-cpp must mount llama-cpp-model read-only.")

required_command = ["--model", "/models/Qwen3-Embedding-0.6B-Q8_0.gguf", "--embedding", "--pooling", "last", "--embd-normalize", "2", "--device", "none", "--offline"]
command = services["llama-cpp"].get("command", [])
if any(argument not in command for argument in required_command):
    raise SystemExit("llama-cpp must use the fixed CPU-only, offline embedding runtime command.")
if services["llama-cpp"].get("gpus") or services["llama-cpp"].get("runtime"):
    raise SystemExit("llama-cpp must not request a GPU runtime.")

api_environment = services["api"].get("environment", {})
if api_environment.get("LlamaCpp__BaseUrl") != "http://llama-cpp:8080/":
    raise SystemExit("api must target the private llama-cpp service.")

published_ports = [name for name, service in services.items() if service.get("ports")]
if published_ports:
    raise SystemExit(f"Coolify Compose must not publish ports; found {published_ports!r}.")

networks = compose.get("networks", {})
if set(networks) != {"default"}:
    raise SystemExit(f"Coolify Compose must use only its implicit default network; found {sorted(networks)!r}.")

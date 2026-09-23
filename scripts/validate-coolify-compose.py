import contextlib
import json
import os
import pathlib
import re
import sys

SOURCE_URL = "https://huggingface.co/Qwen/Qwen3-Embedding-0.6B-GGUF/resolve/370f27d7550e0def9b39c1f16d3fbaa13aa67728/Qwen3-Embedding-0.6B-Q8_0.gguf"
SOURCE_REVISION = "370f27d7550e0def9b39c1f16d3fbaa13aa67728"
EXPECTED_BYTES = "639150592"
EXPECTED_SHA256 = "06507c7b42688469c4e7298b0a1e16deff06caf291cf0a5b278c308249c3e439"
DOWNLOADER_IMAGE = "curlimages/curl@sha256:94e9e444bcba979c2ea12e27ae39bee4cd10bc7041a472c4727a558e213744e6"
SERVER_IMAGE = "ghcr.io/ggml-org/llama.cpp@sha256:c005e79321f8e5731ec49a7f736aaeaac9465926c1e8f4c199c1d8a8996f26ef"

# Production application images: exact repositories and immutable references only.
API_IMAGE_REPOSITORY = "ghcr.io/jesusjbriceno/rag-api"
OPERATOR_IMAGE_REPOSITORY = "ghcr.io/jesusjbriceno/rag-operator"
ADMIN_IMAGE_REPOSITORY = "ghcr.io/jesusjbriceno/rag-adminapp"
APP_IMAGES = {
    "api": API_IMAGE_REPOSITORY,
    "migrate": OPERATOR_IMAGE_REPOSITORY,
}
# Immutable tags only: exact semver (vX.Y.Z[-prerelease]) or develop-<40-char-sha>. Never latest/floating.
IMMUTABLE_TAG_PATTERN = re.compile(r"^(v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?|develop-[0-9a-f]{40})$")
SHA256_DIGEST_PATTERN = re.compile(r"^sha256:[0-9a-f]{64}$")

# The accepted Coolify topology. In paired mode (source view on fd 3) the
# additive admin boundary service is required; the legacy single-view callers
# keep validating the original five-service stack.
REQUIRED_SERVICES_LEGACY = {"postgres", "model-download", "llama-cpp", "migrate", "api"}
REQUIRED_SERVICES_PAIRED = REQUIRED_SERVICES_LEGACY | {"admin"}

# Source-preserving admin environment contract: every entry must be the exact
# `${VAR:?required}` reference; only the PreviousSecret may fall back (to the
# empty string). ADMINAPP_FQDN is the sole public-routing input and may only be
# declared on admin.
ADMINAPP_FQDN = "ADMINAPP_FQDN"
ADMIN_IMAGE_REQUIRED_SOURCE = f"{ADMIN_IMAGE_REPOSITORY}:${{RAG_ADMINAPP_IMAGE_REFERENCE:?required}}"
ADMIN_INTERNAL_ORIGIN = "http://api:8080/"
ADMIN_HEALTHCHECK_COMMAND = "curl --fail --silent --show-error http://127.0.0.1:8080/health/ready"
ADMIN_ENVIRONMENT_SOURCE_REFERENCES = {
    "CloudflareAccess__Issuer": "${CLOUDFLARE_ACCESS__ISSUER:?required}",
    "CloudflareAccess__Audience": "${CLOUDFLARE_ACCESS__AUDIENCE:?required}",
    "CloudflareAccess__Keys__0__KeyId": "${CLOUDFLARE_ACCESS__KEYS__0__KEY_ID:?required}",
    "CloudflareAccess__Keys__0__PublicKeyPem": "${CLOUDFLARE_ACCESS__KEYS__0__PUBLIC_KEY_PEM:?required}",
    "AdminAssertion__Issuer": "${ADMIN_ASSERTION__ISSUER:?required}",
    "AdminAssertion__Audience": "${ADMIN_ASSERTION__AUDIENCE:?required}",
    "AdminAssertion__AppId": "${ADMIN_ASSERTION__APP_ID:?required}",
    "AdminAssertion__KeyId": "${ADMIN_ASSERTION__KEY_ID:?required}",
    "AdminAssertion__PrivateKeyPem": "${ADMIN_ASSERTION__PRIVATE_KEY_PEM:?required}",
    "AdminAppAuth__Apps__0__AppId": "${ADMIN_APP_AUTH__APPS__0__APP_ID:?required}",
    "AdminAppAuth__Apps__0__KeyId": "${ADMIN_APP_AUTH__APPS__0__KEY_ID:?required}",
    "AdminAppAuth__Apps__0__CurrentSecret": "${ADMIN_APP_AUTH__APPS__0__CURRENT_SECRET:?required}",
    "AdminAppAuth__Apps__0__PreviousSecret": "${ADMIN_APP_AUTH__APPS__0__PREVIOUS_SECRET:-}",
    "AdminApi__BaseUrl": "${ADMIN_API__BASE_URL:?required}",
    "AllowedAdminSubjects__0": "${ALLOWED_ADMIN_SUBJECTS__0:?required}",
    ADMINAPP_FQDN: "${ADMINAPP_FQDN:?required}",
}
ADMIN_HEALTHCHECK = {
    "test": ["CMD-SHELL", ADMIN_HEALTHCHECK_COMMAND],
    "interval": "10s",
    "timeout": "5s",
    "retries": 3,
    "start_period": "10s",
}
ADMIN_DEPENDS_ON_SERVICE = {"condition": "service_healthy"}

# Labels that shape public routing (Coolify domains, Traefik/Caddy routers,
# bare domain/FQDN keys) must never appear on the private stack.
PUBLIC_ROUTING_LABEL_PATTERN = re.compile(r"domain|fqdn|router", re.IGNORECASE)


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


def load_compose_view(stream, description):
    try:
        return json.load(stream)
    except ValueError as error:
        raise SystemExit(f"{description} is not valid JSON: {error}.") from error


def load_optional_source_view():
    """Read the source-preserving `--no-interpolate` Compose JSON from file
    descriptor 3. It is optional so the legacy single-view callers keep
    working; when present, the full provenance contract is enforced."""
    # The writer may hand fd 3 over with its shared file offset anywhere;
    # always read the source view from the beginning of regular files.
    with contextlib.suppress(OSError):
        os.lseek(3, 0, os.SEEK_SET)  # not seekable (pipe) or not open: read sequentially below
    try:
        with os.fdopen(3, "r", encoding="utf-8") as stream:
            return load_compose_view(stream, "the source-preserving `--no-interpolate` Compose JSON on file descriptor 3")
    except OSError:
        print(
            "note: no source view on file descriptor 3; skipping source-preserving provenance validation.",
            file=sys.stderr,
        )
        return None


def validate_topology(document, expected_services, view_name):
    services = document.get("services", {})
    if set(services) != expected_services:
        raise SystemExit(
            f"Coolify Compose must contain exactly {sorted(expected_services)!r}; found {sorted(services)!r} ({view_name} view)."
        )
    networks = document.get("networks", {})
    if set(networks) != {"default"}:
        raise SystemExit(
            f"Coolify Compose must use only its implicit default network; found {sorted(networks)!r} ({view_name} view)."
        )


def validate_networks(service, name, view_name):
    if service.get("network_mode"):
        raise SystemExit(
            f"{name} must not declare network_mode; every service joins only the implicit default network ({view_name} view)."
        )
    declared_networks = service.get("networks") or {}
    if isinstance(declared_networks, (dict, list)):
        declared = set(declared_networks)
    else:
        declared = {declared_networks}
    if declared - {"default"}:
        raise SystemExit(
            f"{name} must use only the implicit default network; found {sorted(declared)!r} ({view_name} view)."
        )


def validate_service_denials(services, view_name):
    published_ports = [name for name, service in services.items() if service.get("ports")]
    if published_ports:
        raise SystemExit(f"Coolify Compose must not publish ports; found {published_ports!r} ({view_name} view).")
    for name, service in services.items():
        if service.get("expose"):
            raise SystemExit(
                f"{name} must not declare expose; only admin is routed, through its Coolify FQDN ({view_name} view)."
            )
        validate_networks(service, name, view_name)
        for label in service.get("labels", {}):
            if PUBLIC_ROUTING_LABEL_PATTERN.search(label):
                raise SystemExit(
                    f"{name} must not declare public-routing label {label!r}; "
                    f"public routing is only the admin {ADMINAPP_FQDN} environment reference ({view_name} view)."
                )
        if name != "admin" and ADMINAPP_FQDN in (service.get("environment") or {}):
            raise SystemExit(
                f"{ADMINAPP_FQDN} may only be declared on the admin service; found on {name!r} ({view_name} view)."
            )


def validate_application_image(service, name, expected_repository, view_name):
    if service.get("build"):
        raise SystemExit(f"{name} must not declare a local build; production Compose is pull-only ({view_name} view).")
    image = service.get("image")
    if not image:
        raise SystemExit(f"{name} must reference a pinned GHCR image ({view_name} view).")
    repository, tag, digest = split_image_reference(image)
    if repository != expected_repository:
        raise SystemExit(
            f"{name} must use the {expected_repository} repository; found {repository!r} (repository drift, {view_name} view)."
        )
    if digest is not None:
        if tag:
            raise SystemExit(f"{name} image reference must use either an immutable tag or a digest, not both ({view_name} view).")
        if not SHA256_DIGEST_PATTERN.fullmatch(digest):
            raise SystemExit(f"{name} image digest must be sha256:<64 lowercase hex characters> ({view_name} view).")
    else:
        if not tag or tag == "latest":
            raise SystemExit(f"{name} image tag must not be empty or 'latest' ({view_name} view).")
        if not IMMUTABLE_TAG_PATTERN.fullmatch(tag):
            raise SystemExit(
                f"{name} image tag {tag!r} is not an immutable version tag (vX.Y.Z[-prerelease] or develop-<sha>) ({view_name} view)."
            )
    if service.get("pull_policy") != "always":
        raise SystemExit(
            f"{name} must set pull_policy: always so production pulls and never falls back to a local build ({view_name} view)."
        )


def validate_admin_common(service, view_name):
    if service.get("volumes"):
        raise SystemExit(f"admin must not declare volumes; it is stateless in the Coolify boundary ({view_name} view).")
    environment = service.get("environment") or {}
    missing = [key for key in ADMIN_ENVIRONMENT_SOURCE_REFERENCES if key not in environment]
    if missing:
        raise SystemExit(
            f"admin environment is missing required entries: {missing!r} ({view_name} view)."
        )
    if service.get("healthcheck") != ADMIN_HEALTHCHECK:
        raise SystemExit(
            f"admin healthcheck must be exactly {ADMIN_HEALTHCHECK_COMMAND!r} on 127.0.0.1:8080/health/ready "
            f"with the pinned schedule (interval 10s, timeout 5s, retries 3, start_period 10s) ({view_name} view)."
        )
    depends_on = service.get("depends_on") or {}
    if set(depends_on) != {"api"} or depends_on["api"].get("condition") != "service_healthy":
        raise SystemExit(
            f"admin must depend on api with condition service_healthy and nothing else ({view_name} view)."
        )


def validate_admin_view(service, view_name):
    validate_admin_common(service, view_name)
    environment = service.get("environment") or {}
    if view_name == "rendered":
        validate_application_image(service, "admin", ADMIN_IMAGE_REPOSITORY, view_name)
        if environment.get("AdminApi__BaseUrl") != ADMIN_INTERNAL_ORIGIN:
            raise SystemExit(
                f"admin AdminApi__BaseUrl must be exactly {ADMIN_INTERNAL_ORIGIN!r} in the rendered view; "
                f"found {environment.get('AdminApi__BaseUrl')!r}."
            )
    else:
        if service.get("image") != ADMIN_IMAGE_REQUIRED_SOURCE:
            raise SystemExit(
                f"admin source image must be exactly {ADMIN_IMAGE_REQUIRED_SOURCE!r}; found {service.get('image')!r}."
            )
        for key, expected_reference in ADMIN_ENVIRONMENT_SOURCE_REFERENCES.items():
            actual = environment.get(key)
            if actual != expected_reference:
                raise SystemExit(
                    f"admin source environment entry {key!r} must be the exact reference {expected_reference!r}; "
                    f"found {actual!r} (literal substitution or drift)."
                )


rendered = load_compose_view(sys.stdin, "the rendered `docker compose config` Compose JSON on stdin")
source = load_optional_source_view()

if source is not None:
    views = [("rendered", rendered), ("source", source)]
    expected_services = REQUIRED_SERVICES_PAIRED
else:
    views = [("rendered", rendered)]
    expected_services = REQUIRED_SERVICES_LEGACY

for view_name, document in views:
    validate_topology(document, expected_services, view_name)

# Legacy five-service invariants: rendered values only (the source view keeps
# uninterpolated references).
services = rendered.get("services", {})

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

app_reference_kinds = {}
app_tags = {}
for name, expected_repository in APP_IMAGES.items():
    service = services[name]
    validate_application_image(service, name, expected_repository, "rendered")
    reference = split_image_reference(service["image"])
    if reference[2] is not None:
        app_reference_kinds[name] = "digest"
    else:
        app_reference_kinds[name] = "tag"
        app_tags[name] = reference[1]

if len(set(app_reference_kinds.values())) != 1:
    raise SystemExit("api and migrate must both use immutable tags or both use repository-specific digests.")
if app_reference_kinds["api"] == "tag" and len(set(app_tags.values())) != 1:
    raise SystemExit("api and migrate must use the same immutable version tag.")

api_environment = services["api"].get("environment", {})
if api_environment.get("LlamaCpp__BaseUrl") != "http://llama-cpp:8080/":
    raise SystemExit("api must target the private llama-cpp service.")

for view_name, document in views:
    validate_service_denials(document.get("services", {}), view_name)
    if source is not None:
        validate_admin_view(document["services"]["admin"], view_name)

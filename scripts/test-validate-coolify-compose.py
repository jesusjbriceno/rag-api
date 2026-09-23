#!/usr/bin/env python3
import copy
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
VALIDATOR = ROOT / "scripts" / "validate-coolify-compose.py"
DOWNLOADER_IMAGE = "curlimages/curl@sha256:94e9e444bcba979c2ea12e27ae39bee4cd10bc7041a472c4727a558e213744e6"
SERVER_IMAGE = "ghcr.io/ggml-org/llama.cpp@sha256:c005e79321f8e5731ec49a7f736aaeaac9465926c1e8f4c199c1d8a8996f26ef"
API_IMAGE = "ghcr.io/jesusjbriceno/rag-api"
OPERATOR_IMAGE = "ghcr.io/jesusjbriceno/rag-operator"


def compose(api_image, operator_image):
    return {
        "services": {
            "postgres": {},
            "model-download": {
                "image": DOWNLOADER_IMAGE,
                "user": "0:0",
                "entrypoint": ["/bin/sh", "/scripts/download-llamacpp-model.sh"],
                "volumes": [
                    {"source": "llama-cpp-model", "target": "/models"},
                    {"source": "DOWNLOADER", "target": "/scripts/download-llamacpp-model.sh", "read_only": True},
                ],
            },
            "llama-cpp": {
                "image": SERVER_IMAGE,
                "command": ["--model", "/models/Qwen3-Embedding-0.6B-Q8_0.gguf", "--embedding", "--pooling", "last", "--embd-normalize", "2", "--device", "none", "--offline"],
                "volumes": [{"source": "llama-cpp-model", "target": "/models", "read_only": True}],
            },
            "migrate": {"image": operator_image, "pull_policy": "always"},
            "api": {
                "image": api_image,
                "pull_policy": "always",
                "environment": {"LlamaCpp__BaseUrl": "http://llama-cpp:8080/"},
            },
        },
        "networks": {"default": {}},
    }


class CoolifyValidatorFixture(unittest.TestCase):
    """Shared downloader fixture: the validator always requires the pinned
    downloader script (argv[1]) and gates on its verified content."""

    @classmethod
    def setUpClass(cls):
        cls.temporary_directory = tempfile.TemporaryDirectory()
        cls.downloader = pathlib.Path(cls.temporary_directory.name) / "download-llamacpp-model.sh"
        cls.downloader.write_text(
            "\n".join(
                [
                    "https://huggingface.co/Qwen/Qwen3-Embedding-0.6B-GGUF/resolve/370f27d7550e0def9b39c1f16d3fbaa13aa67728/Qwen3-Embedding-0.6B-Q8_0.gguf",
                    "370f27d7550e0def9b39c1f16d3fbaa13aa67728",
                    "639150592",
                    "06507c7b42688469c4e7298b0a1e16deff06caf291cf0a5b278c308249c3e439",
                    "--proto '=https'",
                    "sha256sum -c -s",
                    'mv -f "$temporary_model" "$model_file"',
                    'mv -f "$temporary_manifest" "$manifest_file"',
                ]
            ),
            encoding="utf-8",
        )

    @classmethod
    def tearDownClass(cls):
        cls.temporary_directory.cleanup()


class CoolifyComposeValidationTests(CoolifyValidatorFixture):
    def validate(self, api_image, operator_image):
        payload = compose(api_image, operator_image)
        payload["services"]["model-download"]["volumes"][1]["source"] = str(self.downloader)
        return subprocess.run(
            [sys.executable, str(VALIDATOR), str(self.downloader)],
            input=json.dumps(payload),
            text=True,
            capture_output=True,
            check=False,
        )

    def assert_rejected(self, api_image, operator_image, message):
        result = self.validate(api_image, operator_image)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(message, result.stderr)

    def test_accepts_matching_immutable_tags(self):
        result = self.validate(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_accepts_repository_specific_sha256_digests(self):
        result = self.validate(f"{API_IMAGE}@sha256:{'a' * 64}", f"{OPERATOR_IMAGE}@sha256:{'b' * 64}")
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_rejects_malformed_digest(self):
        self.assert_rejected(f"{API_IMAGE}@sha256:{'a' * 63}", f"{OPERATOR_IMAGE}@sha256:{'b' * 64}", "image digest must be sha256")

    def test_rejects_wrong_repository(self):
        self.assert_rejected("ghcr.io/example/rag-api:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3", "repository drift")

    def test_rejects_mutable_tag(self):
        self.assert_rejected(f"{API_IMAGE}:latest", f"{OPERATOR_IMAGE}:latest", "must not be empty or 'latest'")

    def test_rejects_tag_combined_with_digest(self):
        self.assert_rejected(f"{API_IMAGE}:v1.2.3@sha256:{'a' * 64}", f"{OPERATOR_IMAGE}@sha256:{'b' * 64}", "not both")

    def test_rejects_mixed_tag_and_digest_references(self):
        self.assert_rejected(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}@sha256:{'b' * 64}", "both use immutable tags or both use repository-specific digests")


# ---------------------------------------------------------------------------
# Unit 4 RED contract — Coolify AdminApp boundary.
#
# The validator is expected to be extended so that it receives a PAIRED view:
#   - the rendered Compose JSON on stdin, and
#   - the source-preserving `--no-interpolate` Compose JSON on file
#     descriptor 3.
#
# Contract exercised by AdminAppCoolifyBoundaryTests:
#   1. The accepted service set is exactly postgres, model-download,
#      llama-cpp, migrate, api, admin. Rejections name the offending or
#      missing service.
#   2. admin: image repository ghcr.io/jesusjbriceno/rag-adminapp with an
#      immutable reference only (semver tag, develop-<40-hex> tag, or
#      sha256 digest), pull_policy: always, no build, no ports, no expose,
#      no volumes, no network_mode, default Compose network only.
#   3. Provenance (source view): every required admin environment entry must
#      be the exact `${VAR:?required}` reference (PreviousSecret the optional
#      empty reference `${VAR:-}`); ADMINAPP_FQDN must be exactly
#      `${ADMINAPP_FQDN:?required}` and may appear only on admin.
#   4. Rendered admin: AdminApi__BaseUrl must be exactly http://api:8080/ and
#      the healthcheck command must be exactly
#      curl --fail --silent --show-error http://127.0.0.1:8080/health/ready.
#   5. No non-admin service may declare ports, network_mode: host, custom or
#      external networks, or public-routing/domain/FQDN-shaped metadata.
# ---------------------------------------------------------------------------

ADMIN_IMAGE_REPOSITORY = "ghcr.io/jesusjbriceno/rag-adminapp"
ADMIN_IMAGE_REQUIRED_SOURCE = f"{ADMIN_IMAGE_REPOSITORY}:${{RAG_ADMINAPP_IMAGE_REFERENCE:?required}}"
ADMINAPP_FQDN_REQUIRED_REFERENCE = "${ADMINAPP_FQDN:?required}"
ADMIN_INTERNAL_ORIGIN = "http://api:8080/"
ADMIN_HEALTHCHECK_COMMAND = "curl --fail --silent --show-error http://127.0.0.1:8080/health/ready"

# Test-only ephemeral placeholders: no production value is ever accepted here.
ADMIN_ENVIRONMENT_RENDERED_PLACEHOLDERS = {
    "CloudflareAccess__Issuer": "https://placeholder-team.cloudflareaccess.com",
    "CloudflareAccess__Audience": "placeholder-access-audience",
    "CloudflareAccess__Keys__0__KeyId": "placeholder-key-id",
    "CloudflareAccess__Keys__0__PublicKeyPem": "placeholder-public-key-pem",
    "AdminAssertion__Issuer": "https://placeholder-issuer.invalid",
    "AdminAssertion__Audience": "placeholder-assertion-audience",
    "AdminAssertion__AppId": "placeholder-app-id",
    "AdminAssertion__KeyId": "placeholder-assertion-key-id",
    "AdminAssertion__PrivateKeyPem": "placeholder-private-key-pem",
    "AdminAppAuth__Apps__0__AppId": "placeholder-client-app-id",
    "AdminAppAuth__Apps__0__KeyId": "placeholder-client-key-id",
    "AdminAppAuth__Apps__0__CurrentSecret": "placeholder-current-secret",
    "AdminAppAuth__Apps__0__PreviousSecret": "",
    "AdminApi__BaseUrl": ADMIN_INTERNAL_ORIGIN,
    "AllowedAdminSubjects__0": "placeholder-subject",
    "ADMINAPP_FQDN": "adminapp.placeholder.invalid",
}

# The exact source references the --no-interpolate view must carry.
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
    "ADMINAPP_FQDN": ADMINAPP_FQDN_REQUIRED_REFERENCE,
}

ADMIN_HEALTHCHECK = {
    "test": ["CMD-SHELL", ADMIN_HEALTHCHECK_COMMAND],
    "interval": "10s",
    "timeout": "5s",
    "retries": 3,
    "start_period": "10s",
}


def admin_service_rendered(image=f"{ADMIN_IMAGE_REPOSITORY}:v1.2.3"):
    return {
        "image": image,
        "pull_policy": "always",
        "environment": dict(ADMIN_ENVIRONMENT_RENDERED_PLACEHOLDERS),
        "depends_on": {"api": {"condition": "service_healthy"}},
        "healthcheck": copy.deepcopy(ADMIN_HEALTHCHECK),
        "restart": "unless-stopped",
    }


def admin_service_source():
    return {
        "image": ADMIN_IMAGE_REQUIRED_SOURCE,
        "pull_policy": "always",
        "environment": dict(ADMIN_ENVIRONMENT_SOURCE_REFERENCES),
        "depends_on": {"api": {"condition": "service_healthy"}},
        "healthcheck": copy.deepcopy(ADMIN_HEALTHCHECK),
        "restart": "unless-stopped",
    }


def paired_compose(admin_image=f"{ADMIN_IMAGE_REPOSITORY}:v1.2.3"):
    """Return (rendered, source) payloads: the canonical six-service stack in
    both the rendered and the source-preserving view."""
    legacy = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
    rendered = copy.deepcopy(legacy)
    source = copy.deepcopy(legacy)
    rendered["services"]["admin"] = admin_service_rendered(admin_image)
    source["services"]["admin"] = admin_service_source()
    return rendered, source


def mutate_both(rendered, source, mutation):
    mutation(rendered)
    mutation(source)


class AdminAppCoolifyBoundaryTests(CoolifyValidatorFixture):
    def validate_paired(self, rendered, source_view):
        """Run the validator with the rendered view on stdin and the
        source-preserving view on file descriptor 3."""
        for payload in (rendered, source_view):
            payload["services"]["model-download"]["volumes"][1]["source"] = str(self.downloader)
        saved_fd = None
        placeholder_fd = None
        try:
            saved_fd = os.dup(3)
        except OSError:
            # fd 3 is free: occupy it so the temporary file (which takes the
            # lowest free descriptor) never lands on fd 3 itself.
            placeholder_fd = os.open(os.devnull, os.O_RDONLY)
        try:
            with tempfile.TemporaryFile() as source_file:
                source_file.write(json.dumps(source_view).encode("utf-8"))
                source_file.flush()
                os.dup2(source_file.fileno(), 3)
                return subprocess.run(
                    [sys.executable, str(VALIDATOR), str(self.downloader)],
                    input=json.dumps(rendered),
                    text=True,
                    capture_output=True,
                    check=False,
                    pass_fds=(3,),
                )
        finally:
            if saved_fd is None:
                if placeholder_fd is not None:
                    os.close(placeholder_fd)  # placeholder_fd == 3: free it again
            else:
                os.dup2(saved_fd, 3)
                os.close(saved_fd)

    def assert_accepted(self, rendered, source_view):
        result = self.validate_paired(rendered, source_view)
        self.assertEqual(result.returncode, 0, result.stderr)

    def assert_rejected(self, rendered, source_view, *message_fragments):
        result = self.validate_paired(rendered, source_view)
        self.assertNotEqual(result.returncode, 0)
        for fragment in message_fragments:
            self.assertIn(fragment, result.stderr)

    # --- canonical topology -------------------------------------------------

    def test_accepts_canonical_six_service_topology(self):
        rendered, source = paired_compose()
        self.assert_accepted(rendered, source)

    def test_accepts_admin_develop_sha_tag(self):
        rendered, source = paired_compose(f"{ADMIN_IMAGE_REPOSITORY}:develop-{'a' * 40}")
        self.assert_accepted(rendered, source)

    def test_accepts_admin_repository_specific_digest(self):
        rendered, source = paired_compose(f"{ADMIN_IMAGE_REPOSITORY}@sha256:{'c' * 64}")
        self.assert_accepted(rendered, source)

    def test_rejects_missing_admin_service(self):
        legacy = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        self.assert_rejected(legacy, legacy, "admin")

    def test_rejects_extra_service_beyond_canonical_set(self):
        rendered, source = paired_compose()
        mutate_both(
            rendered,
            source,
            lambda payload: payload["services"].update({"companion": {"image": f"{API_IMAGE}:v1.2.3"}}),
        )
        self.assert_rejected(rendered, source, "companion")

    def test_rejects_missing_legacy_service_even_with_admin(self):
        rendered, source = paired_compose()
        mutate_both(rendered, source, lambda payload: payload["services"].pop("llama-cpp"))
        self.assert_rejected(rendered, source, "llama-cpp")

    # --- admin image --------------------------------------------------------

    def test_rejects_admin_image_repository_drift(self):
        rendered, source = paired_compose("ghcr.io/example/rag-adminapp:v1.2.3")
        self.assert_rejected(rendered, source, ADMIN_IMAGE_REPOSITORY)

    def test_rejects_admin_image_missing(self):
        rendered, source = paired_compose()
        del rendered["services"]["admin"]["image"]
        self.assert_rejected(rendered, source, "admin must reference")

    def test_rejects_admin_image_mutable_latest(self):
        rendered, source = paired_compose(f"{ADMIN_IMAGE_REPOSITORY}:latest")
        self.assert_rejected(rendered, source, "latest")

    def test_rejects_admin_image_missing_tag(self):
        rendered, source = paired_compose(f"{ADMIN_IMAGE_REPOSITORY}:")
        self.assert_rejected(rendered, source, "must not be empty or 'latest'")

    def test_rejects_admin_image_malformed_digest(self):
        rendered, source = paired_compose(f"{ADMIN_IMAGE_REPOSITORY}@sha256:{'c' * 63}")
        self.assert_rejected(rendered, source, "sha256")

    def test_rejects_admin_image_tag_combined_with_digest(self):
        rendered, source = paired_compose(f"{ADMIN_IMAGE_REPOSITORY}:v1.2.3@sha256:{'c' * 64}")
        self.assert_rejected(rendered, source, "not both")

    def test_rejects_admin_image_mutable_non_semver_tag(self):
        rendered, source = paired_compose(f"{ADMIN_IMAGE_REPOSITORY}:stable")
        self.assert_rejected(rendered, source, "immutable")

    # --- admin structural denial --------------------------------------------

    def test_rejects_admin_local_build(self):
        rendered, source = paired_compose()
        mutate_both(rendered, source, lambda payload: payload["services"]["admin"].update({"build": {"context": "."}}))
        self.assert_rejected(rendered, source, "admin must not declare a local build")

    def test_rejects_admin_missing_pull_policy(self):
        rendered, source = paired_compose()
        mutate_both(rendered, source, lambda payload: payload["services"]["admin"].pop("pull_policy"))
        self.assert_rejected(rendered, source, "pull_policy")

    def test_rejects_admin_wrong_pull_policy(self):
        rendered, source = paired_compose()
        mutate_both(rendered, source, lambda payload: payload["services"]["admin"].update({"pull_policy": "missing"}))
        self.assert_rejected(rendered, source, "pull_policy")

    def test_rejects_admin_published_ports(self):
        rendered, source = paired_compose()
        mutate_both(rendered, source, lambda payload: payload["services"]["admin"].update({"ports": [{"target": 8080, "published": 8080}]}))
        self.assert_rejected(rendered, source, "must not publish ports")

    def test_rejects_admin_expose(self):
        rendered, source = paired_compose()
        mutate_both(rendered, source, lambda payload: payload["services"]["admin"].update({"expose": ["8080"]}))
        self.assert_rejected(rendered, source, "expose")

    def test_rejects_admin_volumes(self):
        rendered, source = paired_compose()
        mutate_both(rendered, source, lambda payload: payload["services"]["admin"].update({"volumes": [{"source": "admin-data", "target": "/data"}]}))
        self.assert_rejected(rendered, source, "admin must not declare volumes")

    def test_rejects_admin_host_network(self):
        rendered, source = paired_compose()
        mutate_both(rendered, source, lambda payload: payload["services"]["admin"].update({"network_mode": "host"}))
        self.assert_rejected(rendered, source, "network_mode")

    def test_rejects_admin_custom_network(self):
        rendered, source = paired_compose()

        def mutation(payload):
            payload["networks"]["admin-internal"] = {}
            payload["services"]["admin"]["networks"] = ["admin-internal"]

        mutate_both(rendered, source, mutation)
        self.assert_rejected(rendered, source, "default network")

    # --- admin environment provenance (source view) --------------------------

    def test_rejects_admin_missing_required_environment_reference(self):
        for key in ADMIN_ENVIRONMENT_SOURCE_REFERENCES:
            with self.subTest(key=key):
                rendered, source = paired_compose()
                mutate_both(
                    rendered,
                    source,
                    lambda payload, key=key: payload["services"]["admin"]["environment"].pop(key),
                )
                self.assert_rejected(rendered, source, key)

    def test_rejects_literal_substitution_in_source_view(self):
        for key, literal in (
            ("CloudflareAccess__Issuer", "https://placeholder-team.cloudflareaccess.com"),
            ("CloudflareAccess__Audience", "placeholder-access-audience"),
            ("AdminAppAuth__Apps__0__CurrentSecret", "placeholder-current-secret"),
            ("AllowedAdminSubjects__0", "placeholder-subject"),
        ):
            with self.subTest(key=key):
                rendered, source = paired_compose()
                source["services"]["admin"]["environment"][key] = literal
                self.assert_rejected(rendered, source, key)

    def test_rejects_previous_secret_literal(self):
        rendered, source = paired_compose()
        source["services"]["admin"]["environment"]["AdminAppAuth__Apps__0__PreviousSecret"] = "placeholder-previous-secret"
        self.assert_rejected(rendered, source, "PreviousSecret")

    # --- FQDN provenance ------------------------------------------------------

    def test_rejects_adminapp_fqdn_absent(self):
        rendered, source = paired_compose()
        mutate_both(
            rendered,
            source,
            lambda payload: payload["services"]["admin"]["environment"].pop("ADMINAPP_FQDN"),
        )
        self.assert_rejected(rendered, source, "ADMINAPP_FQDN")

    def test_rejects_adminapp_fqdn_literal_in_source_view(self):
        rendered, source = paired_compose()
        source["services"]["admin"]["environment"]["ADMINAPP_FQDN"] = "adminapp.placeholder.invalid"
        self.assert_rejected(rendered, source, "ADMINAPP_FQDN")

    def test_rejects_adminapp_fqdn_on_non_admin_service(self):
        rendered, source = paired_compose()
        mutate_both(
            rendered,
            source,
            lambda payload: payload["services"]["api"]["environment"].update({"ADMINAPP_FQDN": ADMINAPP_FQDN_REQUIRED_REFERENCE}),
        )
        self.assert_rejected(rendered, source, "ADMINAPP_FQDN")

    def test_rejects_adminapp_fqdn_alternate_reference_shape(self):
        for wrong_reference in (
            "${ADMINAPP_FQDN:-https://fallback.invalid}",
            "$ADMINAPP_FQDN",
            "${ADMINAPP_FQDN}",
            "${ADMINAPP_HOSTNAME:?required}",
        ):
            with self.subTest(reference=wrong_reference):
                rendered, source = paired_compose()
                source["services"]["admin"]["environment"]["ADMINAPP_FQDN"] = wrong_reference
                self.assert_rejected(rendered, source, "ADMINAPP_FQDN")

    # --- rendered admin URL and healthcheck -----------------------------------

    def test_rejects_non_internal_admin_api_origin(self):
        for base_url in (
            "http://localhost:8080/",
            "http://127.0.0.1:8080/",
            "http://api:8080",
            "http://api:8080/v1/",
            "https://api:8080/",
            "http://api:9090/",
            "http://user:password@api:8080/",
            "http://api:8080/?query=1",
            "http://api:8080#fragment",
            "http://api.example.internal:8080/",
        ):
            with self.subTest(base_url=base_url):
                rendered, source = paired_compose()
                rendered["services"]["admin"]["environment"]["AdminApi__BaseUrl"] = base_url
                self.assert_rejected(rendered, source, ADMIN_INTERNAL_ORIGIN)

    def test_rejects_admin_healthcheck_mismatch(self):
        for command in (
            "curl --fail --silent http://127.0.0.1:8080/health/ready",
            "curl --fail --silent --show-error http://127.0.0.1:8080/api/v1/health/ready",
            "curl --fail --silent --show-error http://127.0.0.1:9090/health/ready",
            "curl --fail --silent --show-error https://127.0.0.1:8080/health/ready",
        ):
            with self.subTest(command=command):
                rendered, source = paired_compose()
                mutate_both(
                    rendered,
                    source,
                    lambda payload, command=command: payload["services"]["admin"]["healthcheck"].update({"test": ["CMD-SHELL", command]}),
                )
                self.assert_rejected(rendered, source, "admin healthcheck")

    # --- non-admin topology denial --------------------------------------------

    def test_rejects_ports_on_non_admin_service(self):
        for service in ("api", "postgres", "llama-cpp"):
            with self.subTest(service=service):
                rendered, source = paired_compose()
                mutate_both(rendered, source, lambda payload, service=service: payload["services"][service].update({"ports": [{"target": 8080, "published": 8080}]}))
                self.assert_rejected(rendered, source, "must not publish ports")

    def test_rejects_host_network_on_non_admin_service(self):
        for service in ("api", "postgres"):
            with self.subTest(service=service):
                rendered, source = paired_compose()
                mutate_both(rendered, source, lambda payload, service=service: payload["services"][service].update({"network_mode": "host"}))
                self.assert_rejected(rendered, source, "network_mode")

    def test_rejects_custom_network_on_non_admin_service(self):
        for service in ("api", "postgres"):
            with self.subTest(service=service):
                rendered, source = paired_compose()

                def mutation(payload, service=service):
                    payload["networks"]["public"] = {}
                    payload["services"][service]["networks"] = ["public"]

                mutate_both(rendered, source, mutation)
                self.assert_rejected(rendered, source, "default network")

    def test_rejects_public_routing_metadata_on_non_admin_service(self):
        for label_key, label_value in (
            ("coolify.domain", "adminapp.placeholder.invalid"),
            ("traefik.http.routers.api.rule", "Host(`adminapp.placeholder.invalid`)"),
            ("caddy.api.custom-domain", "adminapp.placeholder.invalid"),
            ("domain", "adminapp.placeholder.invalid"),
            ("fqdn", "adminapp.placeholder.invalid"),
        ):
            with self.subTest(label=label_key):
                rendered, source = paired_compose()
                mutate_both(
                    rendered,
                    source,
                    lambda payload, label_key=label_key, label_value=label_value: payload["services"]["api"].update({"labels": {label_key: label_value}}),
                )
                self.assert_rejected(rendered, source, label_key)


if __name__ == "__main__":
    unittest.main()

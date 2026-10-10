#!/usr/bin/env python3
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest


ROOT = pathlib.Path(__file__).resolve().parent.parent
VALIDATOR = ROOT / "scripts" / "validate-coolify-compose.py"
DOWNLOADER_SCRIPT_NAME = "download-llamacpp-model.sh"
DOWNLOADER_IMAGE = "curlimages/curl@sha256:94e9e444bcba979c2ea12e27ae39bee4cd10bc7041a472c4727a558e213744e6"
SERVER_IMAGE = "ghcr.io/ggml-org/llama.cpp@sha256:c005e79321f8e5731ec49a7f736aaeaac9465926c1e8f4c199c1d8a8996f26ef"
API_IMAGE = "ghcr.io/jesusjbriceno/rag-api"
OPERATOR_IMAGE = "ghcr.io/jesusjbriceno/rag-operator"
ADMINAPP_IMAGE = "ghcr.io/jesusjbriceno/rag-adminapp"

# Exact required rag-adminapp environment wiring. Every value is operator-supplied at
# deployment time (tests only prove structural wiring, never real domains or secrets).
ADMINAPP_ENVIRONMENT = {
    "AdminBff__UpstreamBaseUrl": "http://rag-api:8080/",
    "AdminBff__SpaRoot": "/app/wwwroot",
    "AdminBff__AllowedOrigin": "OPERATOR_SUPPLIED_ORIGIN",
    "AdminBff__AllowedSubjects__0": "OPERATOR_SUPPLIED_SUBJECT",
    "AdminBff__Signing__AppId": "OPERATOR_SUPPLIED_APP_ID",
    "AdminBff__Signing__KeyId": "OPERATOR_SUPPLIED_KEY_ID",
    "AdminBff__Signing__MachineSecret": "OPERATOR_SUPPLIED_MACHINE_SECRET",
    "CloudflareAccess__Issuer": "OPERATOR_SUPPLIED_CLOUDFLARE_ISSUER",
    "CloudflareAccess__Audience": "OPERATOR_SUPPLIED_CLOUDFLARE_AUDIENCE",
    "CloudflareAccess__Keys__0__KeyId": "OPERATOR_SUPPLIED_CLOUDFLARE_KEY_ID",
    "CloudflareAccess__Keys__0__PublicKeyPem": "OPERATOR_SUPPLIED_CLOUDFLARE_PUBLIC_KEY",
    "AdminAssertion__Issuer": "OPERATOR_SUPPLIED_ASSERTION_ISSUER",
    "AdminAssertion__Audience": "OPERATOR_SUPPLIED_ASSERTION_AUDIENCE",
    "AdminAssertion__AppId": "OPERATOR_SUPPLIED_APP_ID",
    "AdminAssertion__KeyId": "OPERATOR_SUPPLIED_KEY_ID",
    "AdminAssertion__PrivateKeyPem": "OPERATOR_SUPPLIED_ASSERTION_PRIVATE_KEY",
}

# Exact required rag-api admin-plane trust wiring when AdminPlane__Enabled=true.
API_ADMIN_PLANE_ENVIRONMENT = {
    "AdminAppAuth__Apps__0__AppId": "OPERATOR_SUPPLIED_APP_ID",
    "AdminAppAuth__Apps__0__KeyId": "OPERATOR_SUPPLIED_KEY_ID",
    "AdminAppAuth__Apps__0__CurrentSecret": "OPERATOR_SUPPLIED_MACHINE_SECRET",
    "AdminAssertion__Issuer": "OPERATOR_SUPPLIED_ASSERTION_ISSUER",
    "AdminAssertion__Audience": "OPERATOR_SUPPLIED_ASSERTION_AUDIENCE",
    "AdminAssertion__ValidationKeys__0__KeyId": "OPERATOR_SUPPLIED_KEY_ID",
    "AdminAssertion__ValidationKeys__0__PublicKeyPem": "OPERATOR_SUPPLIED_ASSERTION_PUBLIC_KEY",
}


def adminapp_image_like(api_image):
    """Derive the adminapp image reference with the same immutability kind as the API image."""
    if "@sha256:" in api_image:
        return f"{ADMINAPP_IMAGE}@sha256:{'c' * 64}"
    return f"{ADMINAPP_IMAGE}:{api_image.rsplit(':', 1)[1]}"


def compose(api_image, operator_image):
    return {
        "services": {
            "rag-postgres": {},
            "rag-model-download": {
                "image": DOWNLOADER_IMAGE,
                "user": "0:0",
                "entrypoint": ["/bin/sh", "/scripts/download-llamacpp-model.sh"],
                "volumes": [
                    {"type": "volume", "source": "llama-cpp-model", "target": "/models"},
                    {"type": "bind", "source": "SCRIPTS_DIRECTORY", "target": "/scripts", "read_only": True},
                ],
            },
            "rag-llama-cpp": {
                "image": SERVER_IMAGE,
                "command": ["--model", "/models/Qwen3-Embedding-0.6B-Q8_0.gguf", "--embedding", "--pooling", "last", "--embd-normalize", "2", "--device", "none", "--offline"],
                "volumes": [{"type": "volume", "source": "llama-cpp-model", "target": "/models", "read_only": True}],
            },
            "rag-migrate": {"image": operator_image, "pull_policy": "always"},
            "rag-api": {
                "image": api_image,
                "pull_policy": "always",
                "environment": {"LlamaCpp__BaseUrl": "http://rag-llama-cpp:8080/", "AdminPlane__Enabled": "false"},
            },
            "rag-adminapp": {
                "image": adminapp_image_like(api_image),
                "pull_policy": "always",
                "environment": dict(ADMINAPP_ENVIRONMENT),
                "depends_on": {"rag-api": {"condition": "service_healthy"}},
                "healthcheck": {"test": ["CMD-SHELL", "curl --fail --silent --show-error http://127.0.0.1:8080/health"]},
            },
        },
        "networks": {"default": {}},
    }


class CoolifyComposeValidationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary_directory = tempfile.TemporaryDirectory()
        cls.scripts_directory = pathlib.Path(cls.temporary_directory.name) / "scripts"
        cls.scripts_directory.mkdir()
        cls.downloader = cls.scripts_directory / DOWNLOADER_SCRIPT_NAME
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

    def validate(self, api_image, operator_image, scripts_directory=None):
        payload = compose(api_image, operator_image)
        payload["services"]["rag-model-download"]["volumes"][1]["source"] = str(
            scripts_directory if scripts_directory is not None else self.scripts_directory
        )
        script_path = (scripts_directory if scripts_directory is not None else self.scripts_directory) / DOWNLOADER_SCRIPT_NAME
        return subprocess.run(
            [sys.executable, str(VALIDATOR), str(script_path)],
            input=json.dumps(payload),
            text=True,
            capture_output=True,
            check=False,
        )

    def assert_rejected(self, api_image, operator_image, message, scripts_directory=None):
        result = self.validate(api_image, operator_image, scripts_directory=scripts_directory)
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
        self.assert_rejected(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}@sha256:{'b' * 64}", "must all use immutable tags or all use repository-specific digests")

    def test_rejects_wrong_llama_cpp_base_url(self):
        payload = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        payload["services"]["rag-model-download"]["volumes"][1]["source"] = str(self.scripts_directory)
        payload["services"]["rag-api"]["environment"]["LlamaCpp__BaseUrl"] = "http://llama-cpp:8080/"
        result = subprocess.run(
            [sys.executable, str(VALIDATOR), str(self.downloader)],
            input=json.dumps(payload),
            text=True,
            capture_output=True,
            check=False,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("must target the private rag-llama-cpp service", result.stderr)

    def test_rejects_missing_scripts_directory_mount(self):
        self.assert_rejected(
            f"{API_IMAGE}:v1.2.3",
            f"{OPERATOR_IMAGE}:v1.2.3",
            "must bind-mount the repository scripts directory read-only",
            scripts_directory=self.scripts_directory.parent / "does-not-exist",
        )

    def test_rejects_missing_downloader_script(self):
        empty_directory = pathlib.Path(self.temporary_directory.name) / "empty-scripts"
        empty_directory.mkdir()
        self.assert_rejected(
            f"{API_IMAGE}:v1.2.3",
            f"{OPERATOR_IMAGE}:v1.2.3",
            "downloader script is missing",
            scripts_directory=empty_directory,
        )

    def test_rejects_downloader_script_that_is_a_directory(self):
        directory_mount = pathlib.Path(self.temporary_directory.name) / "directory-mount"
        (directory_mount / DOWNLOADER_SCRIPT_NAME).mkdir(parents=True)
        self.assert_rejected(
            f"{API_IMAGE}:v1.2.3",
            f"{OPERATOR_IMAGE}:v1.2.3",
            "was auto-created as a directory",
            scripts_directory=directory_mount,
        )

    def test_rejects_empty_downloader_script(self):
        empty_script_directory = pathlib.Path(self.temporary_directory.name) / "empty-script-scripts"
        empty_script_directory.mkdir()
        (empty_script_directory / DOWNLOADER_SCRIPT_NAME).write_text("", encoding="utf-8")
        self.assert_rejected(
            f"{API_IMAGE}:v1.2.3",
            f"{OPERATOR_IMAGE}:v1.2.3",
            "downloader script is empty",
            scripts_directory=empty_script_directory,
        )

    def validate_payload(self, payload):
        payload["services"]["rag-model-download"]["volumes"][1]["source"] = str(self.scripts_directory)
        return subprocess.run(
            [sys.executable, str(VALIDATOR), str(self.downloader)],
            input=json.dumps(payload),
            text=True,
            capture_output=True,
            check=False,
        )

    def assert_payload_rejected(self, payload, message):
        result = self.validate_payload(payload)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(message, result.stderr)

    def test_rejects_missing_rag_adminapp_service(self):
        payload = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        del payload["services"]["rag-adminapp"]
        self.assert_payload_rejected(payload, "rag-adminapp")

    def test_rejects_adminapp_repository_drift(self):
        payload = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        payload["services"]["rag-adminapp"]["image"] = "ghcr.io/example/rag-adminapp:v1.2.3"
        self.assert_payload_rejected(payload, "repository drift")

    def test_rejects_adminapp_mutable_tag(self):
        payload = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        payload["services"]["rag-adminapp"]["image"] = f"{ADMINAPP_IMAGE}:latest"
        self.assert_payload_rejected(payload, "must not be empty or 'latest'")

    def test_rejects_adminapp_mixed_reference_kind(self):
        payload = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        payload["services"]["rag-adminapp"]["image"] = f"{ADMINAPP_IMAGE}@sha256:{'c' * 64}"
        self.assert_payload_rejected(payload, "must all use immutable tags or all use repository-specific digests")

    def test_rejects_adminapp_local_build(self):
        payload = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        payload["services"]["rag-adminapp"]["build"] = "."
        self.assert_payload_rejected(payload, "must not declare a local build")

    def test_rejects_adminapp_published_ports(self):
        payload = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        payload["services"]["rag-adminapp"]["ports"] = ["8080:8080"]
        self.assert_payload_rejected(payload, "must not publish ports")

    def test_rejects_adminapp_wrong_upstream(self):
        payload = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        payload["services"]["rag-adminapp"]["environment"]["AdminBff__UpstreamBaseUrl"] = "http://localhost:8080/"
        self.assert_payload_rejected(payload, "must target the private rag-api service")

    def test_rejects_adminapp_missing_required_environment(self):
        payload = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        del payload["services"]["rag-adminapp"]["environment"]["AdminBff__AllowedSubjects__0"]
        self.assert_payload_rejected(payload, "missing required admin environment wiring")

    def test_rejects_adminapp_without_rag_api_health_dependency(self):
        payload = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        payload["services"]["rag-adminapp"]["depends_on"] = {"rag-api": {"condition": "service_started"}}
        self.assert_payload_rejected(payload, "must wait for a healthy rag-api")

    def test_rejects_missing_adminplane_enabled_switch(self):
        payload = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        del payload["services"]["rag-api"]["environment"]["AdminPlane__Enabled"]
        self.assert_payload_rejected(payload, "AdminPlane__Enabled")

    def test_rejects_invalid_adminplane_enabled_value(self):
        payload = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        payload["services"]["rag-api"]["environment"]["AdminPlane__Enabled"] = "yes"
        self.assert_payload_rejected(payload, "AdminPlane__Enabled must be exactly 'true' or 'false'")

    def test_rejects_enabled_adminplane_without_trust_material(self):
        payload = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        payload["services"]["rag-api"]["environment"]["AdminPlane__Enabled"] = "true"
        self.assert_payload_rejected(payload, "missing required admin trust configuration")

    def test_rejects_custom_networks(self):
        payload = compose(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}:v1.2.3")
        payload["services"]["rag-model-download"]["volumes"][1]["source"] = str(self.scripts_directory)
        payload["networks"] = {"default": {}, "coolify": {"external": True}}
        result = subprocess.run(
            [sys.executable, str(VALIDATOR), str(self.downloader)],
            input=json.dumps(payload),
            text=True,
            capture_output=True,
            check=False,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("must use only its implicit default network", result.stderr)


if __name__ == "__main__":
    unittest.main()

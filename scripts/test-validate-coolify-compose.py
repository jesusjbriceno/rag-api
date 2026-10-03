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
                "environment": {"LlamaCpp__BaseUrl": "http://rag-llama-cpp:8080/"},
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
        self.assert_rejected(f"{API_IMAGE}:v1.2.3", f"{OPERATOR_IMAGE}@sha256:{'b' * 64}", "both use immutable tags or both use repository-specific digests")

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

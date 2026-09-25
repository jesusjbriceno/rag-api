#!/usr/bin/env python3
import json
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


class CoolifyComposeValidationTests(unittest.TestCase):
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


if __name__ == "__main__":
    unittest.main()

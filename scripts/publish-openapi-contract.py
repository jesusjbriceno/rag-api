#!/usr/bin/env python3
import argparse
import base64
import os
import pathlib
import urllib.error
import urllib.request

REPO_ROOT = pathlib.Path(os.environ.get("RAG_API_REPO", "/opt/wf/rag-api"))
SOURCE = REPO_ROOT / "docs" / "api" / "Rag.Api.json"
DESTINATION = "_obsidian/Desarrollo/03_Projects/rag-api/Rag.Api.json"
SECRETS_FILE = pathlib.Path(os.environ.get("NEXTCLOUD_ENV", "~/.hermes/secrets/nextcloud.env")).expanduser()
REQUIRED_KEYS = ("NEXTCLOUD_URL", "NEXTCLOUD_USER", "NEXTCLOUD_PASS")


def credentials():
    if not SECRETS_FILE.is_file():
        raise SystemExit(f"Nextcloud credentials file not found: {SECRETS_FILE}.")
    values = {}
    for line in SECRETS_FILE.read_text(encoding="utf-8").splitlines():
        if "=" in line:
            key, value = line.strip().split("=", 1)
            values[key] = value
    missing = [key for key in REQUIRED_KEYS if not values.get(key)]
    if missing:
        raise SystemExit(f"Missing keys in {SECRETS_FILE}: {', '.join(missing)}.")
    return values


def authorization(user, password):
    token = base64.b64encode(f"{user}:{password}".encode()).decode()
    return "Basic " + token


def main():
    parser = argparse.ArgumentParser(description="Publish the generated OpenAPI contract to the Nextcloud vault.")
    parser.add_argument("--dry-run", action="store_true", help="print the source size and destination without uploading")
    arguments = parser.parse_args()

    if not SOURCE.is_file():
        raise SystemExit(
            f"Generated contract not found: {SOURCE}. Run "
            "'ASPNETCORE_ENVIRONMENT=OpenApiGeneration dotnet build src/Rag.Api/Rag.Api.csproj' first."
        )
    payload = SOURCE.read_bytes()

    if arguments.dry_run:
        print(f"Source size: {len(payload)} bytes")
        print(f"Destination: {DESTINATION}")
        return

    values = credentials()
    if not values["NEXTCLOUD_URL"].startswith(("https://", "http://")):
        raise SystemExit("NEXTCLOUD_URL in the credentials file must be an http(s) URL.")
    destination_url = f"{values['NEXTCLOUD_URL']}/remote.php/dav/files/{values['NEXTCLOUD_USER']}/{DESTINATION}"
    # pi-lens-ignore: Semgrep:python.lang.security.audit.dynamic-urllib-use-detected.dynamic-urllib-use-detected
    request = urllib.request.Request(destination_url, data=payload, method="PUT")
    request.add_header("Authorization", authorization(values["NEXTCLOUD_USER"], values["NEXTCLOUD_PASS"]))
    try:
        # pi-lens-ignore: Semgrep:python.lang.security.audit.dynamic-urllib-use-detected.dynamic-urllib-use-detected
        with urllib.request.urlopen(request) as response:
            print(f"Uploaded {len(payload)} bytes to {DESTINATION}: HTTP {response.status}.")
    except urllib.error.HTTPError as error:
        raise SystemExit(f"Upload rejected for {DESTINATION}: HTTP {error.code}.") from None
    except urllib.error.URLError as error:
        raise SystemExit(f"Upload failed for {DESTINATION}: {error.reason}.") from None


if __name__ == "__main__":
    main()

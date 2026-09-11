#!/usr/bin/env python3
"""Validate NUGET_KEY and decide whether csproj versions should publish to NuGet.org."""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
import xml.etree.ElementTree as ET

NUGET_ORG = "https://www.nuget.org"
NUGET_FLAT = "https://api.nuget.org/v3-flatcontainer"
CREATE_KEY = NUGET_ORG + "/api/v2/package/create-verification-key/{id}"
CREATE_KEY_VERSION = NUGET_ORG + "/api/v2/package/create-verification-key/{id}/{version}"
PUBLISH = NUGET_ORG + "/api/v2/package"


def load_ci():
    candidates = [
        Path(__file__).with_name("run-plugin-ci.py"),
        Path(".ci-tools/.github/scripts/run-plugin-ci.py"),
        Path(".github/scripts/run-plugin-ci.py"),
    ]
    path = next((item for item in candidates if item.is_file()), None)
    if path is None:
        raise SystemExit("::error::Could not load run-plugin-ci.py")
    spec = importlib.util.spec_from_file_location("run_plugin_ci", path)
    if spec is None or spec.loader is None:
        raise SystemExit(f"::error::Could not load {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


ci = load_ci()


def local_name(tag: str) -> str:
    return tag.split("}")[-1]


def property_text(path: Path, names: set[str]) -> dict[str, str]:
    values: dict[str, str] = {}
    try:
        tree = ET.parse(path)
    except (ET.ParseError, OSError) as exc:
        print(f"::warning::Could not parse {path}: {exc}")
        return values
    for element in tree.iter():
        name = local_name(element.tag)
        if name not in names:
            continue
        if element.attrib.get("Include") or element.attrib.get("include"):
            continue
        if element.text and element.text.strip() and not element.text.strip().startswith("$("):
            values[name] = element.text.strip()
    return values


def directory_build_props(plugin_root: Path, csproj: Path) -> list[Path]:
    props: list[Path] = []
    current = csproj.parent.resolve()
    root = plugin_root.resolve()
    seen: set[Path] = set()
    while True:
        candidate = current / "Directory.Build.props"
        if candidate.is_file() and candidate not in seen:
            props.append(candidate)
            seen.add(candidate)
        if current == root or current.parent == current:
            break
        current = current.parent
    props.reverse()
    return props


def package_identity(plugin_root: Path, csproj: Path) -> tuple[str, str]:
    merged: dict[str, str] = {}
    for path in directory_build_props(plugin_root, csproj) + [csproj]:
        merged.update(property_text(path, {"PackageId", "AssemblyName", "Version", "PackageVersion"}))
    package_id = merged.get("PackageId") or merged.get("AssemblyName") or csproj.stem
    version = merged.get("PackageVersion") or merged.get("Version")
    if not version:
        raise SystemExit(f"::error::{csproj} has no Version or PackageVersion")
    return package_id, version


def normalize_version(version: str) -> str:
    core, sep, pre = version.strip().partition("-")
    core = core.split("+", 1)[0]
    parts = [part for part in core.split(".") if part != ""]
    while len(parts) > 3 and parts[-1] == "0":
        parts.pop()
    normalized = ".".join(parts).lower()
    if sep:
        normalized += "-" + pre.split("+", 1)[0].lower()
    return normalized


def parse_nuget_version(version: str) -> tuple[tuple[int, ...], tuple[tuple[int, int, str], ...]]:
    """Return a comparable (release, prerelease) pair. A release sorts above a prerelease."""
    text = version.strip()
    if not text:
        raise ValueError("empty version")
    core, _, pre = text.partition("-")
    core = core.split("+", 1)[0]
    pre = pre.split("+", 1)[0]
    numbers: list[int] = []
    for part in core.split("."):
        if not part.isdigit():
            raise ValueError(f"unsupported version: {version}")
        numbers.append(int(part))
    while len(numbers) < 3:
        numbers.append(0)
    prerelease: list[tuple[int, int, str]] = []
    if pre:
        for part in pre.split("."):
            if part.isdigit():
                prerelease.append((0, int(part), ""))
            else:
                prerelease.append((1, 0, part.lower()))
    return tuple(numbers), tuple(prerelease)


def compare_nuget_versions(left: str, right: str) -> int:
    """Return <0 if left < right, 0 if equal, >0 if left > right."""
    left_release, left_pre = parse_nuget_version(left)
    right_release, right_pre = parse_nuget_version(right)
    if left_release != right_release:
        return (left_release > right_release) - (left_release < right_release)
    if not left_pre and not right_pre:
        return 0
    if not left_pre:
        return 1
    if not right_pre:
        return -1
    return (left_pre > right_pre) - (left_pre < right_pre)


def max_nuget_version(versions: set[str]) -> str | None:
    latest: str | None = None
    for version in versions:
        if latest is None or compare_nuget_versions(version, latest) > 0:
            latest = version
    return latest


def write_output(name: str, value: str) -> None:
    print(f"{name}={value}")
    path = os.environ.get("GITHUB_OUTPUT")
    if not path:
        return
    with open(path, "a", encoding="utf-8") as handle:
        handle.write(f"{name}={value}\n")


def http(method: str, url: str, api_key: str | None = None, data: bytes | None = None) -> tuple[int, str]:
    headers = {
        "User-Agent": "MauiEssentials-check-nuget-release",
        "X-NuGet-Protocol-Version": "4.1.0",
    }
    if api_key:
        headers["X-NuGet-ApiKey"] = api_key
    request = urllib.request.Request(url, data=data, headers=headers, method=method)
    context = ssl.create_default_context()
    try:
        with urllib.request.urlopen(request, context=context, timeout=30) as response:
            body = response.read().decode("utf-8", errors="replace")
            return response.getcode() or 200, body
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        return exc.code, body
    except urllib.error.URLError as exc:
        fail(f"Could not reach nuget.org ({exc.reason})")


def fail(message: str) -> None:
    print(f"::error::{message}")
    raise SystemExit(1)


def validate_key(api_key: str, package_id: str, version: str) -> None:
    encoded_id = urllib.parse.quote(package_id)
    encoded_version = urllib.parse.quote(version)
    url = CREATE_KEY_VERSION.format(id=encoded_id, version=encoded_version)
    status, body = http("POST", url, api_key=api_key)
    snippet = " ".join(body.split())[:300]
    print(f"NUGET_KEY check for {package_id} {version}: HTTP {status}")
    if status in {200, 201}:
        print("NUGET_KEY is accepted by nuget.org")
        return
    if status in {401, 403}:
        detail = snippet or "nuget.org rejected the key"
        fail(f"NUGET_KEY is expired, invalid, or not allowed to publish {package_id}. {detail}")
    if status == 404:
        status, body = http("POST", CREATE_KEY.format(id=encoded_id), api_key=api_key)
        snippet = " ".join(body.split())[:300]
        print(f"NUGET_KEY check for {package_id}: HTTP {status}")
        if status in {200, 201}:
            print("NUGET_KEY is accepted by nuget.org")
            return
        if status in {401, 403}:
            detail = snippet or "nuget.org rejected the key"
            fail(f"NUGET_KEY is expired, invalid, or not allowed to publish {package_id}. {detail}")
        status, body = http("PUT", PUBLISH, api_key=api_key, data=b"")
        snippet = " ".join(body.split())[:300]
        print(f"NUGET_KEY publish probe: HTTP {status}")
        if status in {401, 403}:
            detail = snippet or "nuget.org rejected the key"
            fail(f"NUGET_KEY is expired or invalid. {detail}")
        if status in {400, 409, 415}:
            print("NUGET_KEY is accepted by nuget.org")
            return
        fail(f"Could not validate NUGET_KEY (HTTP {status}). {snippet}")
    fail(f"Could not validate NUGET_KEY (HTTP {status}). {snippet}")


def published_versions(package_id: str) -> set[str]:
    url = f"{NUGET_FLAT}/{package_id.lower()}/index.json"
    status, body = http("GET", url)
    if status == 404:
        return set()
    if status != 200:
        fail(f"Could not list NuGet.org versions for {package_id} (HTTP {status})")
    try:
        payload = json.loads(body)
    except json.JSONDecodeError:
        fail(f"NuGet.org returned invalid version list for {package_id}")
    versions = payload.get("versions") or []
    return {normalize_version(str(item)) for item in versions}


def self_test() -> None:
    assert compare_nuget_versions("1.0.4", "1.0.3") > 0
    assert compare_nuget_versions("1.0.10", "1.0.9") > 0
    assert compare_nuget_versions("1.0.4", "1.0.4") == 0
    assert compare_nuget_versions("1.0.4-preview", "1.0.4") < 0
    assert compare_nuget_versions("1.0.4-preview", "1.0.3") > 0
    assert compare_nuget_versions("1.0.4", "1.0.5") < 0
    assert max_nuget_version(set()) is None
    assert max_nuget_version({"1.0.3", "1.0.10", "1.0.9"}) == "1.0.10"
    print("self-test passed")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--plugin-root", default=".")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0

    plugin_root = Path(args.plugin_root).resolve()
    if not plugin_root.is_dir():
        fail(f"Plugin folder not found: {plugin_root}")

    api_key = os.environ.get("NUGET_KEY", "").strip()
    if not api_key:
        fail("NUGET_KEY secret is empty. Add a valid nuget.org API key under Settings → Secrets and variables → Actions.")

    src_projects = [path for path in ci.find_csprojs(plugin_root, "src") if ci.is_packable(path)]
    if not src_projects:
        fail(f"No packable src/*.csproj under {plugin_root}")

    packages = [package_identity(plugin_root, csproj) for csproj in src_projects]
    print("Release packages from csproj:")
    for package_id, version in packages:
        print(f"  {package_id} {version}")

    validate_key(api_key, packages[0][0], packages[0][1])

    already = []
    for package_id, version in packages:
        listed = published_versions(package_id)
        deployed = max_nuget_version(listed)
        present = normalize_version(version) in listed
        if deployed is None:
            print(f"{package_id} has no versions on NuGet.org; csproj {version} will publish")
            continue
        print(f"{package_id}: NuGet.org {deployed}; csproj {version}")
        if present or compare_nuget_versions(version, deployed) == 0:
            already.append(f"{package_id} {version}")
    if already:
        fail(
            "csproj version matches a version already deployed to NuGet.org. "
            "Bump Version / PackageVersion before build: " + ", ".join(already)
        )

    write_output("should_publish", "true")
    print("NUGET_KEY is valid and the csproj version is not on NuGet.org")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

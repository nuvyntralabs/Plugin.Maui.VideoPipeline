#!/usr/bin/env python3
"""Push packed nupkg/snupkg to nuget.org and GitHub Packages.

nuget.org receives the nupkg and matching snupkg (when one exists).
GitHub Packages receives the nupkg only — that registry does not host
symbol packages. Both pushes use --skip-duplicate.

Template, source-generator, and PackAsTool (.Cli) packages may omit snupkg.
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path


NUGET_ORG = "https://api.nuget.org/v3/index.json"


def fail(message: str) -> None:
    print(f"::error::{message}")
    raise SystemExit(1)


def skip_symbols(nupkg_stem: str) -> bool:
    name = nupkg_stem.lower()
    return ".templates." in name or ".sourcegenerators." in name or ".cli." in name


def github_packages_source(owner: str) -> str:
    return f"https://nuget.pkg.github.com/{owner}/index.json"


def find_packages(packages_dir: Path) -> tuple[list[Path], list[Path]]:
    nupkgs = sorted(
        path
        for path in packages_dir.rglob("*.nupkg")
        if path.is_file() and not path.name.endswith(".snupkg")
    )
    snupkgs = sorted(path for path in packages_dir.rglob("*.snupkg") if path.is_file())
    return nupkgs, snupkgs


def run_push(path: Path, api_key: str, source: str) -> None:
    print(f"Pushing {path} to {source}")
    result = subprocess.run(
        [
            "dotnet",
            "nuget",
            "push",
            str(path),
            "--api-key",
            api_key,
            "--source",
            source,
            "--skip-duplicate",
        ],
        check=False,
    )
    if result.returncode != 0:
        fail(f"dotnet nuget push failed for {path} ({source})")


def self_test() -> None:
    assert skip_symbols("Plugin.Maui.MVVMExpress.Templates.1.3.0")
    assert skip_symbols("Plugin.Maui.HttpForge.SourceGenerators.1.0.0")
    assert skip_symbols("Plugin.Maui.Performance.Cli.1.0.7")
    assert not skip_symbols("Plugin.Maui.GeoLocator.1.0.8")
    assert not skip_symbols("Plugin.Maui.Performance.1.0.7")
    assert github_packages_source("nuvyntralabs") == (
        "https://nuget.pkg.github.com/nuvyntralabs/index.json"
    )
    print("self-test passed")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--packages-dir", default="packages")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0

    packages_dir = Path(args.packages_dir)
    nuget_key = os.environ.get("NUGET_KEY", "").strip()
    github_token = os.environ.get("GITHUB_TOKEN", "").strip()
    owner = (
        os.environ.get("GITHUB_REPOSITORY_OWNER")
        or os.environ.get("GITHUB_REPOSITORY", "nuvyntralabs/MauiEssentials").split("/", 1)[0]
    ).strip()

    if not nuget_key:
        fail("NUGET_KEY secret is empty. Add a valid nuget.org API key under Settings → Secrets and variables → Actions.")
    if not github_token:
        fail("GITHUB_TOKEN is empty. Grant packages: write on the publish job.")
    if not owner:
        fail("Could not determine the GitHub Packages organization (GITHUB_REPOSITORY_OWNER).")
    if not packages_dir.is_dir():
        fail(f"No package folder at {packages_dir}.")

    nupkgs, _snupkgs = find_packages(packages_dir)
    if not nupkgs:
        print("Files in package folder:")
        for path in sorted(packages_dir.rglob("*")):
            if path.is_file():
                print(f"  {path}")
        fail(f"No .nupkg files found in {packages_dir}.")

    print("Packages to push:")
    for path in sorted(packages_dir.rglob("*")):
        if path.suffix in {".nupkg", ".snupkg"} or path.name.endswith(".snupkg"):
            print(f"  {path}")

    github_source = github_packages_source(owner)
    print(f"nuget.org: {NUGET_ORG}")
    print(f"GitHub Packages: {github_source}")

    for pkg in nupkgs:
        symbol = pkg.with_name(pkg.name[: -len(".nupkg")] + ".snupkg")
        run_push(pkg, nuget_key, NUGET_ORG)
        if skip_symbols(pkg.stem):
            print(f"Skipping symbols for {pkg.name}")
        elif not symbol.is_file():
            fail(f"Missing symbol package for {pkg}: {symbol}")
        else:
            run_push(symbol, nuget_key, NUGET_ORG)
        run_push(pkg, github_token, github_source)

    print(
        "GitHub Packages defaults to private on first publish. "
        f"Set each package Public at https://github.com/orgs/{owner}/packages if the repo is public."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

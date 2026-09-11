#!/usr/bin/env python3
"""Fail unless all packable src package versions match, and emit that version."""

from __future__ import annotations

import argparse
import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def fail(message: str) -> None:
    print(f"::error::{message}")
    raise SystemExit(1)


def local_name(tag: str) -> str:
    return tag.split("}")[-1]


def first_property(path: Path, name: str) -> str | None:
    if not path.is_file():
        return None
    try:
        tree = ET.parse(path)
    except (ET.ParseError, OSError) as exc:
        fail(f"Could not parse {path}: {exc}")
    for element in tree.iter():
        if local_name(element.tag) != name:
            continue
        if element.attrib.get("Include") or element.attrib.get("include"):
            continue
        text = (element.text or "").strip()
        if text and not text.startswith("$("):
            return text
    return None


def write_output(name: str, value: str) -> None:
    print(f"{name}={value}")
    output = os.environ.get("GITHUB_OUTPUT")
    if not output:
        return
    with open(output, "a", encoding="utf-8") as handle:
        handle.write(f"{name}={value}\n")


def packable(csproj: Path) -> bool:
    flag = first_property(csproj, "IsPackable")
    test = first_property(csproj, "IsTestProject")
    if test == "true":
        return False
    return flag != "false"


def project_version(csproj: Path) -> str | None:
    return first_property(csproj, "PackageVersion") or first_property(csproj, "Version")


def collect(plugin_root: Path) -> dict[str, str]:
    versions: dict[str, str] = {}
    src = plugin_root / "src"
    if not src.is_dir():
        fail(f"No src/ under {plugin_root}")
    packable_projects = [
        path
        for path in sorted(src.rglob("*.csproj"))
        if "bin" not in path.parts and "obj" not in path.parts and packable(path)
    ]
    if not packable_projects:
        fail("No packable src/*.csproj")

    props = plugin_root / "Directory.Build.props"
    product = first_property(props, "PackageVersion") or first_property(props, "Version")
    if product:
        versions[str(props.relative_to(plugin_root)) + " Version"] = product

    for path in packable_projects:
        version = project_version(path) or product
        if not version:
            fail(f"{path.relative_to(plugin_root)} has no Version or PackageVersion")
        versions[str(path.relative_to(plugin_root))] = version
    return versions


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--plugin-root", default=".")
    parser.add_argument(
        "--scope",
        default="library",
        help="Ignored for single-package plugins. library = packable src versions.",
    )
    args = parser.parse_args()
    plugin_root = Path(args.plugin_root).resolve()
    if not plugin_root.is_dir():
        fail(f"Plugin folder not found: {plugin_root}")

    versions = collect(plugin_root)
    expected = next(iter(versions.values()))
    print("Version alignment (library, must all equal):")
    mismatched: list[str] = []
    for label, version in versions.items():
        mark = "OK" if version == expected else "MISMATCH"
        print(f"  [{mark}] {version}  {label}")
        if version != expected:
            mismatched.append(f"{label}={version}")
    if mismatched:
        fail(f"Packable package versions must match {expected}. Fix: " + ", ".join(mismatched))

    write_output("version", expected)
    print(f"Version {expected} is aligned (library)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

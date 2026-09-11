#!/usr/bin/env python3
"""Build, test, and optionally pack one MauiEssentials plugin folder."""

from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def local_name(tag: str) -> str:
    return tag.split("}")[-1]


def parse_csproj_values(csproj: Path, names: set[str]) -> dict[str, list[str]]:
    values: dict[str, list[str]] = {name: [] for name in names}
    try:
        tree = ET.parse(csproj)
    except ET.ParseError as exc:
        print(f"::warning::Could not parse {csproj}: {exc}")
        return values
    for element in tree.iter():
        name = local_name(element.tag)
        if name in names and element.text:
            values[name].append(element.text.strip())
    return values


def declared_tfms(csproj: Path) -> set[str]:
    raw = parse_csproj_values(csproj, {"TargetFramework", "TargetFrameworks"})
    tfms: set[str] = set()
    for blob in raw["TargetFramework"] + raw["TargetFrameworks"]:
        for part in blob.split(";"):
            item = part.strip()
            if item and not item.startswith("$("):
                tfms.add(item)
    return tfms


def is_packable(csproj: Path) -> bool:
    raw = parse_csproj_values(csproj, {"IsPackable", "IsTestProject"})
    if any(value.lower() == "true" for value in raw["IsTestProject"]):
        return False
    if any(value.lower() == "false" for value in raw["IsPackable"]):
        return False
    return True


def is_dotnet_tool(csproj: Path) -> bool:
    raw = parse_csproj_values(csproj, {"PackAsTool"})
    return any(value.lower() == "true" for value in raw["PackAsTool"])


def package_id(csproj: Path) -> str:
    raw = parse_csproj_values(csproj, {"PackageId", "AssemblyName"})
    if raw["PackageId"]:
        return raw["PackageId"][0]
    if raw["AssemblyName"]:
        return raw["AssemblyName"][0]
    return csproj.stem


def package_version(csproj: Path) -> str:
    raw = parse_csproj_values(csproj, {"PackageVersion", "Version"})
    if raw["PackageVersion"]:
        return raw["PackageVersion"][0]
    if raw["Version"]:
        return raw["Version"][0]
    return ""


def nupkg_for(package: str, version: str, outputs: list[Path]) -> Path | None:
    expected = f"{package}.{version}.nupkg".lower()
    for path in outputs:
        if path.name.lower() == expected:
            return path
    return None


def tfm_matches(requested: str, actual: str) -> bool:
    if actual == requested:
        return True
    if "-" not in requested:
        return False
    if not actual.startswith(requested):
        return False
    rest = actual[len(requested) :]
    return not rest or rest[0].isdigit()


def matching_tfms(requested: list[str], actual: set[str]) -> list[str]:
    matched: list[str] = []
    for item in requested:
        for tfm in sorted(actual):
            if tfm_matches(item, tfm) and tfm not in matched:
                matched.append(tfm)
    return matched


def find_csprojs(root: Path, folder: str) -> list[Path]:
    base = root / folder
    if not base.is_dir():
        return []
    projects = []
    for path in sorted(base.rglob("*.csproj")):
        parts = set(path.parts)
        if "bin" in parts or "obj" in parts:
            continue
        projects.append(path)
    return projects


def run(command: list[str], cwd: Path) -> None:
    print(f"::group::{' '.join(command)}")
    print(f"cwd={cwd}")
    completed = subprocess.run(command, cwd=cwd)
    print("::endgroup::")
    if completed.returncode != 0:
        raise SystemExit(completed.returncode)


def project_references(csproj: Path) -> list[Path]:
    refs: list[Path] = []
    try:
        tree = ET.parse(csproj)
    except ET.ParseError:
        return refs
    for element in tree.iter():
        if local_name(element.tag) != "ProjectReference":
            continue
        include = element.attrib.get("Include") or element.attrib.get("include")
        if include:
            # csproj paths use Windows separators; Path on Linux treats '\' as literal.
            refs.append((csproj.parent / include.replace("\\", "/")).resolve())
    return refs


def collect_graph(projects: list[Path]) -> list[Path]:
    seen: set[Path] = set()
    queue = list(projects)
    ordered: list[Path] = []
    while queue:
        csproj = queue.pop()
        if csproj in seen or not csproj.is_file():
            continue
        seen.add(csproj)
        ordered.append(csproj)
        queue.extend(project_references(csproj))
    return ordered


def rewrite_target_frameworks(csproj: Path, tfms: list[str]) -> None:
    """Pin TargetFrameworks to the TFMs this runner will actually build.

    MSBuild evaluates every TFM in the csproj (NETSDK1178 on Linux).
    `dotnet pack -p:TargetFramework=` still writes every original TFM into the
    nuspec (NU5048 / NU5026). A single unconditional list avoids both.
    """
    if not tfms:
        return
    text = csproj.read_text(encoding="utf-8")
    if "<TargetFrameworks" not in text:
        return
    joined = ";".join(tfms)
    if text.count("<TargetFrameworks") == 1 and f"<TargetFrameworks>{joined}</TargetFrameworks>" in text:
        return
    updated, count = re.subn(
        r"\s*<TargetFrameworks\b[^>]*>.*?</TargetFrameworks>",
        "",
        text,
        flags=re.DOTALL,
    )
    if count == 0:
        return
    updated = re.sub(
        r"(<PropertyGroup>)",
        rf"\1\n    <TargetFrameworks>{joined}</TargetFrameworks>",
        updated,
        count=1,
    )
    csproj.write_text(updated, encoding="utf-8")
    print(f"Pinned {csproj} TargetFrameworks to {joined}", flush=True)


def pin_matching_tfms(projects: list[Path], requested: list[str]) -> None:
    for csproj in collect_graph(projects):
        matches = matching_tfms(requested, declared_tfms(csproj))
        if matches:
            rewrite_target_frameworks(csproj, matches)


def build_project(csproj: Path, tfm: str | None, configuration: str) -> None:
    command = [
        "dotnet",
        "build",
        str(csproj),
        "-c",
        configuration,
        "--nologo",
        "--verbosity",
        "minimal",
        # snupkg requires portable PDBs, not Windows or embedded symbols.
        "-p:DebugType=portable",
        "-p:DebugSymbols=true",
    ]
    if tfm:
        command.extend(["-f", tfm])
    run(command, csproj.parent)


def test_project(csproj: Path, tfm: str | None, configuration: str) -> None:
    command = ["dotnet", "test", str(csproj), "-c", configuration, "--nologo", "--verbosity", "minimal"]
    if tfm:
        command.extend(["-f", tfm])
    run(command, csproj.parent)


def pack_project(csproj: Path, configuration: str, *, include_symbols: bool = True) -> None:
    command = [
        "dotnet",
        "pack",
        str(csproj),
        "-c",
        configuration,
        "--nologo",
        "--verbosity",
        "minimal",
        "--no-build",
    ]
    if include_symbols:
        command.extend(
            [
                "-p:IncludeSymbols=true",
                "-p:SymbolPackageFormat=snupkg",
            ]
        )
    else:
        command.append("-p:IncludeSymbols=false")
    run(command, csproj.parent)


def packaged_outputs(plugin_root: Path, pattern: str) -> list[Path]:
    return [
        path
        for path in sorted(plugin_root.rglob(pattern))
        if "bin" in path.parts or "artifacts" in path.parts
    ]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--plugin-root", required=True)
    parser.add_argument("--frameworks", default="net10.0")
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--pack", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--test", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--build-unmatched", action=argparse.BooleanOptionalAction, default=False)
    parser.add_argument(
        "--stage-dir",
        default="",
        help="Copy packed .nupkg and .snupkg files into this directory.",
    )
    args = parser.parse_args()

    plugin_root = Path(args.plugin_root).resolve()
    if not plugin_root.is_dir():
        print(f"::error::Plugin folder not found: {plugin_root}")
        return 1

    requested = [item.strip() for item in args.frameworks.split(",") if item.strip()]
    src_projects = find_csprojs(plugin_root, "src")
    test_projects = find_csprojs(plugin_root, "tests")
    if not src_projects:
        print(f"::error::No src/*.csproj under {plugin_root}")
        return 1

    pin_matching_tfms(src_projects + test_projects, requested)

    print(f"Plugin: {plugin_root.name}", flush=True)
    print(f"Frameworks: {', '.join(requested)}", flush=True)
    print(f"Source projects: {len(src_projects)}", flush=True)
    print(f"Test projects: {len(test_projects)}", flush=True)

    if args.test:
        if not test_projects:
            print(f"::error::No tests/*.csproj under {plugin_root}")
            return 1
        tested_any = False
        for csproj in test_projects:
            actual = declared_tfms(csproj)
            matches = matching_tfms(requested, actual)
            if matches:
                for tfm in matches:
                    test_project(csproj, tfm, args.configuration)
                    tested_any = True
            elif any(item == "net10.0" for item in requested):
                test_project(csproj, None, args.configuration)
                tested_any = True
            else:
                print(f"::notice::Skipping tests {csproj.name}; TFMs {sorted(actual)} do not match {requested}")
        if not tested_any:
            print(f"::error::No tests ran for {plugin_root.name} ({', '.join(requested)})")
            return 1

    if args.pack:
        built_any = False
        for csproj in src_projects:
            actual = declared_tfms(csproj)
            matches = matching_tfms(requested, actual)
            if matches:
                for tfm in matches:
                    build_project(csproj, tfm, args.configuration)
                    built_any = True
            elif args.build_unmatched or any(item == "net10.0" for item in requested):
                build_project(csproj, None, args.configuration)
                built_any = True
            else:
                print(f"::notice::Skipping {csproj.name}; TFMs {sorted(actual)} do not match {requested}")
        if not built_any:
            print(f"::error::No projects built for {plugin_root.name} ({', '.join(requested)})")
            return 1

    packed_any = False
    packed_library = False
    if args.pack:
        for csproj in src_projects:
            if not is_packable(csproj):
                continue
            actual = declared_tfms(csproj)
            matches = matching_tfms(requested, actual)
            should_pack = bool(matches) or any(item == "net10.0" for item in requested)
            if not should_pack:
                continue
            tool = is_dotnet_tool(csproj)
            pack_project(csproj, args.configuration, include_symbols=not tool)
            packed_any = True
            if not tool:
                packed_library = True
        if not packed_any:
            print(f"::error::No packages generated for {plugin_root.name}")
            return 1
        packages = packaged_outputs(plugin_root, "*.nupkg")
        symbols = packaged_outputs(plugin_root, "*.snupkg")
        print("Generated packages:", flush=True)
        for path in packages:
            print(f"  {path}", flush=True)
        print("Generated symbol packages:", flush=True)
        for path in symbols:
            print(f"  {path}", flush=True)
        if packed_library and not symbols:
            print(f"::error::No symbol packages (.snupkg) generated for {plugin_root.name}")
            return 1
        for csproj in src_projects:
            if not is_packable(csproj):
                continue
            identity = package_id(csproj)
            version = package_version(csproj)
            found = nupkg_for(identity, version, packages)
            if found is None:
                print(f"::error::Missing nupkg for {identity} {version} ({csproj.name})")
                return 1
            print(f"Packed {identity} {version} -> {found.name}", flush=True)
        stage_dir = args.stage_dir.strip()
        if stage_dir:
            stage = Path(stage_dir)
            if not stage.is_absolute():
                stage = Path.cwd() / stage
            stage.mkdir(parents=True, exist_ok=True)
            print(f"Staging packages to {stage}", flush=True)
            for path in packages + symbols:
                dest = stage / path.name
                shutil.copy2(path, dest)
                print(f"  {dest}", flush=True)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())

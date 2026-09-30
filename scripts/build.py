#!/usr/bin/env python3
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import plistlib
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src/HistoryClipboard.Desktop/HistoryClipboard.Desktop.csproj"
VERSION = "1.0.0"
RIDS = ("win-x64", "osx-arm64", "osx-x64")


def run(*args):
    print(" ".join(map(str, args)), flush=True)
    subprocess.run(list(map(str, args)), cwd=ROOT, check=True,
                   env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "AVALONIA_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1"})


def make_mac_bundle(published, rid):
    if platform.system() != "Darwin":
        raise SystemExit("Для упаковки и подписи .app выполните скрипт на macOS.")
    bundle = ROOT / "artifacts/dist" / rid / "History Clipboard.app"
    macos = bundle / "Contents/MacOS"
    resources = bundle / "Contents/Resources"
    macos.mkdir(parents=True, exist_ok=True)
    resources.mkdir(parents=True, exist_ok=True)
    shutil.copytree(published, macos, dirs_exist_ok=True)
    shutil.copytree(ROOT / "licenses", resources / "Licenses", dirs_exist_ok=True)
    shutil.copy2(ROOT / "docs/USER_GUIDE.md", resources / "Инструкция.md")
    shutil.copy2(ROOT / "THIRD_PARTY_NOTICES.md", resources / "THIRD_PARTY_NOTICES.md")
    icon = ROOT / "assets/HistoryClipboard.icns"
    shutil.copy2(icon, resources / "HistoryClipboard.icns")
    info = {
        "CFBundleName": "History Clipboard", "CFBundleDisplayName": "History Clipboard",
        "CFBundleIdentifier": "io.historyclipboard.desktop", "CFBundleExecutable": "HistoryClipboard",
        "CFBundlePackageType": "APPL", "CFBundleShortVersionString": VERSION,
        "CFBundleVersion": VERSION, "CFBundleIconFile": "HistoryClipboard.icns",
        "LSMinimumSystemVersion": "12.0", "NSHighResolutionCapable": True,
        "NSPrincipalClass": "NSApplication",
        "NSPasteboardUsageDescription": "History Clipboard сохраняет скопированный текст в локальную историю при включённом сборе."
    }
    with (bundle / "Contents/Info.plist").open("wb") as stream:
        plistlib.dump(info, stream)
    files = sorted(macos.rglob("*"), key=lambda file: file.name == "HistoryClipboard")
    for file in files:
        if file.is_file():
            subprocess.run(["codesign", "--force", "--sign", "-", "--timestamp=none", str(file)], check=True, capture_output=True)
    run("codesign", "--force", "--sign", "-", "--timestamp=none", bundle)
    run("codesign", "--verify", "--deep", "--strict", bundle)
    archive = ROOT / "artifacts" / f"HistoryClipboard-{VERSION}-{rid}.zip"
    run("ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", bundle, archive)
    return archive, macos / "HistoryClipboard"


def build(dotnet, rid):
    published = ROOT / "artifacts/publish" / rid
    if published.exists():
        shutil.rmtree(published)
    command = [dotnet, "publish", PROJECT, "-c", "Release", "-r", rid, "--self-contained", "true",
               "--no-restore", "-o", published, "-p:DebugType=None", "-p:DebugSymbols=false"]
    if rid == "win-x64":
        command += ["-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true"]
    run(*command)
    if rid.startswith("osx"):
        archive, executable = make_mac_bundle(published, rid)
    else:
        dist = ROOT / "artifacts/dist" / rid
        dist.mkdir(parents=True, exist_ok=True)
        executable = dist / "HistoryClipboard.exe"
        shutil.copy2(published / "HistoryClipboard.exe", executable)
        shutil.copy2(ROOT / "docs/USER_GUIDE.md", dist / "Инструкция.md")
        shutil.copy2(ROOT / "THIRD_PARTY_NOTICES.md", dist / "THIRD_PARTY_NOTICES.md")
        shutil.copytree(ROOT / "licenses", dist / "Licenses", dirs_exist_ok=True)
        archive = ROOT / "artifacts" / f"HistoryClipboard-{VERSION}-{rid}.zip"
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as stream:
            for file in sorted(dist.rglob("*")):
                if file.is_file():
                    stream.write(file, arcname=str(file.relative_to(dist)))
    host_rid = ("osx-arm64" if platform.machine() == "arm64" else "osx-x64") if platform.system() == "Darwin" else "win-x64"
    tested = rid == host_rid
    if tested:
        run(executable, "--self-test", "--report", ROOT / "artifacts" / f"self-test-{rid}.json")
    return {
        "rid": rid, "archive": archive.name, "executable": str(executable.relative_to(ROOT)),
        "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(), "bytes": archive.stat().st_size,
        "local_storage_self_test": tested,
        "signature": "ad-hoc, без нотариализации Apple" if rid.startswith("osx") else "без подписи Authenticode"
    }


def main():
    parser = argparse.ArgumentParser(description="Самостоятельные сборки History Clipboard")
    parser.add_argument("--rid", choices=RIDS, action="append")
    parser.add_argument("--dotnet", default=shutil.which("dotnet") or str(ROOT / ".tools/dotnet/dotnet"))
    args = parser.parse_args()
    run(args.dotnet, "restore", "--locked-mode")
    run(args.dotnet, "test", ROOT / "tests/HistoryClipboard.Tests/HistoryClipboard.Tests.csproj", "-c", "Release", "--no-restore")
    results = [build(args.dotnet, rid) for rid in args.rid or RIDS]
    (ROOT / "artifacts/build-manifest.json").write_text(json.dumps({"version": VERSION, "artifacts": results}, ensure_ascii=False, indent=2) + "\n")
    (ROOT / "artifacts/SHA256SUMS.txt").write_text("".join(f"{r['sha256']}  {r['archive']}\n" for r in results))


if __name__ == "__main__":
    main()

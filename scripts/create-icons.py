#!/usr/bin/env python3
from pathlib import Path
import struct
import subprocess

root = Path(__file__).resolve().parents[1]
assets = root / "assets"
iconset = root / "artifacts/icon/HistoryClipboard.iconset"
assets.mkdir(exist_ok=True)
iconset.mkdir(parents=True, exist_ok=True)


def run(*args):
    subprocess.run(list(map(str, args)), cwd=root, check=True, stdout=subprocess.DEVNULL)


run("swift", root / "scripts/create-icon.swift", assets / "HistoryClipboard.png")
run("sips", "-z", "1024", "1024", assets / "HistoryClipboard.png")
for size in (16, 32, 128, 256, 512):
    for factor in (1, 2):
        name = f"icon_{size}x{size}{'@2x' if factor == 2 else ''}.png"
        run("sips", "-z", size * factor, size * factor, assets / "HistoryClipboard.png", "--out", iconset / name)
run("iconutil", "-c", "icns", iconset, "-o", assets / "HistoryClipboard.icns")
png = (iconset / "icon_256x256.png").read_bytes()
(assets / "HistoryClipboard.ico").write_bytes(struct.pack("<HHH", 0, 1, 1) + struct.pack("<BBBBHHII", 0, 0, 0, 0, 1, 32, len(png), 22) + png)
print("Созданы PNG, ICO и ICNS.")

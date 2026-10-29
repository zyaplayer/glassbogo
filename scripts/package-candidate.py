"""Build an installable candidate archive from artifacts/publish.

Deliberately separate from package-candidate.ps1: that script refuses to run
without a matching 30-minute isolated endurance record. This one always states
its limits in the manifest instead of implying the endurance gate passed.

Usage:
    python scripts/package-candidate.py [--publish DIR] [--output DIR] [--label NAME]
"""

import argparse
import hashlib
import json
import os
import subprocess
import sys
import time
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
REQUIRED = ["GlassBogo.exe", "GlassBogo.dll", "THIRD-PARTY-NOTICES.txt", "README.md", "licenses"]


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish", default=str(REPO / "artifacts" / "publish"))
    parser.add_argument("--output", default=str(REPO / "artifacts" / "releases"))
    parser.add_argument("--label", default="")
    args = parser.parse_args()

    publish = Path(args.publish).resolve()
    output = Path(args.output).resolve()
    if not publish.is_dir():
        sys.exit("发布目录不存在，先运行 build.ps1 或 dotnet publish")
    missing = [name for name in REQUIRED if not (publish / name).exists()]
    if missing:
        sys.exit("发布目录缺少：" + "、".join(missing))

    dll = publish / "GlassBogo.dll"
    dll_hash = sha256(dll)
    version = os.environ.get("GLASSBOGO_VERSION", "0.1.1")
    try:
        commit = subprocess.run(["git", "rev-parse", "--short", "HEAD"], cwd=REPO,
                                capture_output=True, text=True, check=True).stdout.strip()
        dirty = subprocess.run(["git", "status", "--porcelain"], cwd=REPO,
                               capture_output=True, text=True, check=True).stdout.strip()
    except subprocess.CalledProcessError:
        commit, dirty = "unknown", ""

    label = args.label or version
    name = f"GlassBogo-{label}-{dll_hash[:12]}-win-x64"
    output.mkdir(parents=True, exist_ok=True)
    archive = output / (name + ".zip")
    manifest_path = output / (name + ".json")
    hash_path = output / (name + ".zip.sha256")
    for path in (archive, manifest_path, hash_path):
        if path.exists():
            sys.exit(f"{path.name} 已存在；换个 --output 或删掉旧包")

    files = sorted(p for p in publish.rglob("*") if p.is_file())
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
        for path in files:
            bundle.write(path, path.relative_to(publish).as_posix())

    archive_hash = sha256(archive)
    hash_path.write_text(f"{archive_hash}  {archive.name}\n", encoding="utf-8")
    manifest = {
        "product": "GlassBogo",
        "kind": "installable candidate (limits listed, endurance gate NOT claimed)",
        "version": version,
        "commit": commit,
        "workingTreeDirty": bool(dirty),
        "architecture": "win-x64",
        "createdUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "sourceDirectory": str(publish),
        "fileCount": len(files),
        "dllSha256": dll_hash,
        "archiveSha256": archive_hash,
        "install": "解压后运行 artifacts/publish 内同名的 install.ps1，或直接覆盖 %LOCALAPPDATA%\\Programs\\GlassBogo",
        "limits": [
            "30 分钟隔离长测未包含在本脚本内；需要 package-candidate.ps1 的耐久门槛请先跑 test.ps1 -Endurance",
            "未代码签名，无在线更新",
            "真实 DWM 玻璃观感未验收",
        ],
    }
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print("候选包:", archive)
    print("清单  :", manifest_path)
    print("校验  :", hash_path)
    print("文件数:", len(files), "| dll:", dll_hash[:16], "| zip:", archive_hash[:16])


if __name__ == "__main__":
    main()

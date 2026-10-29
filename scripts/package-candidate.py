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
import re
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


def check_endurance(directory, dll_hash):
    """Enforce the same gates as package-candidate.ps1 before calling a build a candidate."""
    report_path = directory / "report.json"
    events_path = directory / "events.log"
    if not report_path.exists() or not events_path.exists():
        sys.exit("长测目录缺少 report.json / events.log")
    report = json.loads(report_path.read_text(encoding="utf-8"))
    events = events_path.read_text(encoding="utf-8", errors="replace")
    if "PASS completed" not in events:
        sys.exit("长测未通过：events.log 缺少 PASS completed")
    if report.get("stressSeconds", 0) < 1800:
        sys.exit("长测时长不足 1800 秒：%s" % report.get("stressSeconds"))
    if report.get("baselineWindows") != report.get("finalWindows"):
        sys.exit("长测期间窗口数增长：%s -> %s" % (report.get("baselineWindows"), report.get("finalWindows")))
    match = re.search(r"assemblySHA256=([A-F0-9]{64})", events)
    if not match or match.group(1) != dll_hash:
        sys.exit("长测记录的构建指纹与发布 DLL 不一致，拒绝打包")
    return {
        "directory": str(directory),
        "stressSeconds": report.get("stressSeconds"),
        "iterations": report.get("iterations"),
        "calendarCycles": report.get("calendarCycles"),
        "idlePrivateMiB": report.get("idlePrivateMB"),
        "idleCpuPercent": report.get("idleCpuPercent"),
        "p95Milliseconds": report.get("p95Milliseconds"),
        "peakPrivateMiB": report.get("peakPrivateMB"),
        "handles": [report.get("baselineHandles"), report.get("finalHandles")],
        "windows": [report.get("baselineWindows"), report.get("finalWindows")],
        "assemblySha256": match.group(1),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish", default=str(REPO / "artifacts" / "publish"))
    parser.add_argument("--output", default=str(REPO / "artifacts" / "releases"))
    parser.add_argument("--label", default="")
    parser.add_argument("--endurance", default="", help="30 分钟隔离长测目录；给了才校验并标记正式候选")
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
    endurance = None
    if args.endurance:
        endurance = check_endurance(Path(args.endurance).resolve(), dll_hash)
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
        "kind": "candidate with 30-minute isolated endurance record" if endurance
                else "installable candidate (limits listed, endurance gate NOT claimed)",
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
        "endurance": endurance,
        "limits": (
            [
                "未代码签名，无在线更新",
                "真实 DWM 玻璃观感与物理 DPI 切换未验收",
                "长测跑在隔离桌面，不代表真实桌面图标收纳流程已验收",
            ] if endurance else
            [
                "30 分钟隔离长测未包含在本脚本内；需要 package-candidate.ps1 的耐久门槛请先跑 test.ps1 -Endurance",
                "未代码签名，无在线更新",
                "真实 DWM 玻璃观感未验收",
            ]
        ),
    }
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print("候选包:", archive)
    print("清单  :", manifest_path)
    print("校验  :", hash_path)
    print("文件数:", len(files), "| dll:", dll_hash[:16], "| zip:", archive_hash[:16])


if __name__ == "__main__":
    main()

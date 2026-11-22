#!/usr/bin/env python3
"""将干净的自包含发布目录生成更新专用包，不从安装目录收集任何用户文件。"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import stat
import zipfile


def validate_entrypoint(path: Path, runtime: str):
    with path.open("rb") as source:
        header = source.read(64)
        if runtime.startswith("linux-"):
            expected = 183 if runtime.endswith("arm64") else 62
            if len(header) < 20 or header[:4] != b"\x7fELF" or header[4:6] != b"\x02\x01" or int.from_bytes(header[18:20], "little") != expected:
                raise ValueError("程序 ELF 架构与更新包不匹配。")
        else:
            if len(header) < 64 or header[:2] != b"MZ":
                raise ValueError("程序不是 Windows PE 文件。")
            source.seek(int.from_bytes(header[60:64], "little"))
            signature = source.read(6)
            expected = 0xAA64 if runtime.endswith("arm64") else 0x8664
            if signature[:4] != b"PE\0\0" or int.from_bytes(signature[4:6], "little") != expected:
                raise ValueError("程序 PE 架构与更新包不匹配。")


def create(source: Path, output: Path, app: str, version: str, runtime: str):
    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
        raise ValueError("更新版本必须为四段数字。")
    entry = "ClassShout.Classroom" if app == "classroom" else "ClassShout.Teacher.Desktop"
    if runtime.startswith("win-"):
        entry += ".exe"
    if not (source / entry).is_file() or not (source / "ClassShout.Core.dll").is_file():
        raise ValueError("不是完整应用发布目录。")
    validate_entrypoint(source / entry, runtime)
    files = []
    paths = []
    for path in sorted(source.rglob("*")):
        if path.is_symlink():
            raise ValueError("更新发布目录不能包含符号链接。")
        if not path.is_file():
            continue
        relative = path.relative_to(source).as_posix()
        if relative.split("/")[0].lower() in {"logs", "plugins", "plugin-data", "data", "config", "runtime"}:
            raise ValueError("更新包只能来自干净的自包含发布物。")
        if path.name in {"bells.json", "classroom.json", "teacher.json", "appearance.json", "relay-config.json", "classshout-update.json"}:
            raise ValueError("更新包不得包含用户设置或旧清单。")
        executable = path.name == entry or bool(path.stat().st_mode & stat.S_IXUSR)
        files.append(dict(path=relative, sha256=hashlib.sha256(path.read_bytes()).hexdigest(), executable=executable))
        paths.append((path, relative))
    manifest = dict(format=1, appId=app, version=version, runtime=runtime, entryPoint=entry, files=files)
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path, relative in paths:
            archive.write(path, relative)
        archive.writestr("classshout-update.json", json.dumps(manifest, ensure_ascii=False, indent=2))
    print(f"更新包已生成：{output}（{len(files)} 个文件）")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--app", choices=["teacher", "classroom"], required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--runtime", choices=["win-x64", "win-arm64", "linux-x64", "linux-arm64"], required=True)
    args = parser.parse_args()
    create(args.source, args.output, args.app, args.version, args.runtime)

#!/usr/bin/env python3
"""先验证各平台附件清单，再合并并生成统一校验清单。"""
import argparse
import hashlib
from pathlib import Path
import re
import shutil


def merge(sources, output):
    files = {}
    for source in sources:
        manifest = (source / "SHA256SUMS.txt").read_text(encoding="utf-8").splitlines()
        recorded = set()
        for line in manifest:
            match = re.fullmatch(r"([a-fA-F0-9]{64})  ([A-Za-z0-9_.-]+)", line)
            if not match:
                raise ValueError("附件清单格式非法。")
            digest, name = match.groups()
            if name in recorded or name in files or name == "SHA256SUMS.txt":
                raise ValueError("附件名称重复。")
            path = source / name
            if path.is_symlink() or hashlib.sha256(path.read_bytes()).hexdigest().lower() != digest.lower():
                raise ValueError(f"附件校验失败：{name}")
            recorded.add(name)
            files[name] = path
        actual = {path.name for path in source.iterdir() if path.name != "SHA256SUMS.txt"}
        if actual != recorded:
            raise ValueError("附件目录与清单不一致。")
    output.mkdir(parents=True, exist_ok=False)
    lines = []
    for name, path in sorted(files.items()):
        shutil.copy(path, output / name)
        lines.append(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {name}\n")
    (output / "SHA256SUMS.txt").write_text("".join(lines), encoding="utf-8")
    print(f"已验证并合并 {len(files)} 个发布附件。")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("sources", type=Path, nargs="+")
    options = parser.parse_args()
    merge(options.sources, options.output)

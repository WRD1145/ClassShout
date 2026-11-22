#!/usr/bin/env python3
"""不安装进系统，解开全部 Linux 格式并启动真实程序；验证更新包应用后的启动。"""
import argparse
import hashlib
import json
import os
import platform
from pathlib import Path
import signal
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
import zipfile


def probe(executable, state):
    state.mkdir(parents=True, exist_ok=True)
    environment = os.environ.copy()
    environment.update(CLASSSHOUT_DATA_DIR=str(state / "data"), APPIMAGE_EXTRACT_AND_RUN="1")
    log_path = state / "startup.log"
    with log_path.open("w") as log:
        process = subprocess.Popen([str(executable)], env=environment, stdout=log, stderr=log, start_new_session=True)
        try:
            end = time.monotonic() + 8
            while process.poll() is None and time.monotonic() < end:
                time.sleep(0.1)
            if process.poll() is not None:
                raise RuntimeError(f"启动失败：{executable}\n{log_path.read_text()}")
            print(f"[通过] 真实启动并持续运行：{executable.name}", flush=True)
        finally:
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            try:
                process.wait(timeout=8)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()


def extract_rpm(archive, destination):
    # newc 归档由 rpm2cpio 解码；只接受本次构建的常规文件和目录。
    data = subprocess.check_output(["rpm2cpio", str(archive)])
    position = 0
    while position + 110 <= len(data):
        header = data[position:position + 110]
        if header[:6] not in (b"070701", b"070702"):
            raise ValueError("RPM CPIO 格式异常。")
        size = int(header[54:62], 16)
        namesize = int(header[94:102], 16)
        mode = int(header[14:22], 16)
        position += 110
        name = data[position:position + namesize - 1].decode()
        position = (position + namesize + 3) & ~3
        if name == "TRAILER!!!":
            return
        relative = Path(name.removeprefix("./"))
        if relative.is_absolute() or ".." in relative.parts:
            raise ValueError("RPM 路径越界。")
        path = destination / relative
        if stat.S_ISDIR(mode):
            path.mkdir(parents=True, exist_ok=True)
        elif stat.S_ISREG(mode):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data[position:position + size])
            path.chmod(mode & 0o777)
        else:
            raise ValueError("RPM 存在未预期的特殊文件。")
        position = (position + size + 3) & ~3
    raise ValueError("RPM 缺少归档结束标记。")


def main(directory, architecture):
    directory = directory.resolve()
    product = f"ClassShout.Classroom-linux-{architecture}"
    with tempfile.TemporaryDirectory(prefix="cs-linux-package-test-") as folder:
        root = Path(folder)
        tar = root / "tar"
        with tarfile.open(directory / f"{product}.tar.gz") as archive:
            archive.extractall(tar, filter="data")
        probe(tar / "ClassShout.Classroom/ClassShout.Classroom", root / "tar-state")
        for kind in ("deb", "rpm"):
            destination = root / kind
            destination.mkdir()
            if kind == "deb":
                subprocess.run(["dpkg-deb", "-x", str(directory / f"{product}.deb"), str(destination)], check=True)
            else:
                extract_rpm(directory / f"{product}.rpm", destination)
            app = destination / "opt/classshout-classroom"
            assert (app / "classshout-package-manager").read_text().strip() == kind
            assert (destination / "usr/share/applications/classshout-classroom.desktop").is_file()
            assert (destination / "usr/bin/classshout-classroom").stat().st_mode & stat.S_IXUSR
            probe(app / "ClassShout.Classroom", root / f"{kind}-state")
        probe(directory / f"{product}.AppImage", root / "appimage-state")
        payload = root / "payload"
        with zipfile.ZipFile(directory / f"{product}-update.zip") as archive:
            archive.extractall(payload)
        manifest = json.loads((payload / "classshout-update.json").read_text())
        for file in manifest["files"]:
            path = payload / file["path"]
            assert hashlib.sha256(path.read_bytes()).hexdigest() == file["sha256"]
            if file["executable"]:
                path.chmod(0o755)
        target = root / "old-install"
        target.mkdir()
        (target / "ClassShout.Classroom").write_text("旧入口占位")
        (target / "bells.json").write_text("保留本机铃声")
        result = root / "update-result.txt"
        request = root / "request.json"
        request.write_text(json.dumps(dict(payload=str(payload), target=str(target), appId="classroom", version=manifest["version"],
            parentId=0, parentStartTicks=0, resultPath=str(result), restart=True)))
        environment = os.environ.copy()
        environment["CLASSSHOUT_DATA_DIR"] = str(root / "updated-state")
        restarted = []
        try:
            subprocess.run([str(payload / "ClassShout.Classroom"), "--apply-update", str(request)], env=environment, check=True, timeout=90)
            assert "更新成功" in result.read_text()
            assert (target / "bells.json").read_text() == "保留本机铃声"
            for item in Path("/proc").iterdir():
                if not item.name.isdigit():
                    continue
                try:
                    if (item / "exe").resolve() == target / "ClassShout.Classroom":
                        restarted.append(int(item.name))
                except (OSError, RuntimeError):
                    pass
            assert restarted, "更新器没有真正启动新程序。"
            time.sleep(8)
            assert all(Path(f"/proc/{pid}/exe").resolve() == target / "ClassShout.Classroom" for pid in restarted)
            print("[通过] 更新器自动重启了新程序，运行 8 秒未退出。", flush=True)
        finally:
            for pid in restarted:
                try:
                    os.kill(pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
        print("[通过] 更新包实际覆盖、保留配置并启动；五种 Linux 产物验收通过。", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--architecture", choices=["x64", "arm64"], default="arm64" if platform.machine().lower() in ("aarch64", "arm64") else "x64")
    options = parser.parse_args()
    main(options.directory, options.architecture)

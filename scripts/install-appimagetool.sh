#!/usr/bin/env bash
# 固定官方工具版本与 SHA-256，下载损坏或上游替换附件时停止构建。
set -euo pipefail
target="${1:?请指定 appimagetool 输出路径}"
mkdir -p "$(dirname "$target")"
case "$(uname -m)" in
  x86_64) tool_arch=x86_64; digest=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0 ;;
  aarch64|arm64) tool_arch=aarch64; digest=f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158 ;;
  *) echo 'AppImage 构建宿主只支持 x64 或 ARM64。' >&2; exit 1 ;;
esac
curl -fLsS --retry 3 "https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-$tool_arch.AppImage" -o "$target"
printf '%s  %s\n' "$digest" "$target" | sha256sum -c -
chmod +x "$target"

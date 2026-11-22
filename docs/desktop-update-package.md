# 桌面更新与 Linux 打包

## 行为与范围

桌面端统一加载 FluentAvalonia，Android 保持触控布局。
Windows 教师端、教室端及 Linux 教室端优先选择对应的 `*-update.zip`。
旧发行版没有专用更新附件时仍提供原下载入口；旧客户端需要先手动升级一次。
AppImage 下载新版整文件，deb/rpm 交给包管理器安装，不能绕过权限修改系统目录。

更新包包含自包含应用的完整文件集和运行时，不是二进制差分。
这样可以从 Windows 原有平铺安装或带私有 runtime 的便携目录升级，不依赖目标机安装 SDK。
旧目录中未登记的文件不会删除；配置、插件与日志不会进入覆盖范围。

## 校验与事务

1. 更新卡片使用当前镜像、代理下载发行版 `SHA256SUMS.txt` 和更新附件。
2. 校验包级 SHA-256，然后检查 `classshout-update.json` 中的应用、版本、架构和逐文件哈希。
3. 拒绝绝对路径、路径穿越、符号链接、大小写碰撞、未登记文件、配置文件和过大归档。
4. 从暂存目录启动新程序的 `--apply-update` 入口，收到就绪信号后才关闭原程序。
5. 更新进程等待原 PID 退出，备份已有文件再替换，失败时回滚。
6. 回滚受阻时保留备份并记录位置；正常完成的缓存七天后清理，带故障备份的目录不自动清理。

SHA-256 验证完整性，发行版来源仍依赖用户选择的镜像和 HTTPS；它不是独立的软件签名。
文件替换无法承诺断电原子性，更新期间应保持供电。Windows 图形运行与 Linux 系统安装验收分别放在对应 CI 节点执行。

## 打包

Windows 继续使用 `scripts/pack.ps1`。选择 `-Installer` 会复用安装器的自包含 payload 生成两个更新包；不选该参数也生成更新包。

Linux 使用原生工具，不在 Windows 节点模拟 deb/rpm：

```bash
bash scripts/install-appimagetool.sh artifacts/appimagetool.AppImage
python scripts/pack-linux.py --app classroom --appimagetool artifacts/appimagetool.AppImage
python scripts/test-linux-packages.py dist/linux-native/release
```

依赖：Python 3、.NET 10 SDK、dpkg-deb、rpmbuild、rpm2cpio、squashfs-tools，以及运行 Avalonia 所需的图形会话。
脚本使用 AppImage 工具 1.9.1，固定 SHA-256；不修改系统字体、包数据库或真实应用配置。

CI 分别上传 Windows/Android 和 Linux 附件。合并脚本先验证两份清单，拒绝重复或漏登附件，再生成统一 `SHA256SUMS.txt`。
Linux 包保留原有 tar.gz，并增加 AppImage、deb、rpm 和 update.zip；中继服务器继续提供 tar.gz。

## 验收

```bash
dotnet build ClassShout.DesktopOnly.slnf -c Release -p:EnableWindowsTargeting=true
bash scripts/regress-linux.sh
dotnet run --project tools/ClassShout.DesignPreview -c Release --no-build -- artifacts/preview
```

更新安全断言位于 `tools/ClassShout.EndToEnd/DesktopUpdates.cs`。
`scripts/test-linux-packages.py` 解包启动四种发行格式，再实际应用 update.zip、检查铃声保留并启动更新结果。
Windows CI 解开两个更新包后启动 GUI；Ubuntu 安装 deb，Fedora 隔离容器安装 rpm 并运行诊断。

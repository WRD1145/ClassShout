# Arch Linux 开发环境

主仓库与文档站分别位于 `~/CodeX/ClassShout`、`~/CodeX/ClassShoutDocs`。
旧的《开发环境配置指南》记录的是 Windows 机器，不应直接用于 Linux。

## 工具与环境变量

项目 `global.json` 要求 .NET SDK 10.0.401 或同一主次版本的更新功能带。
Arch 软件源的 10.0.1xx 不满足这个要求；当前使用微软官方用户级 SDK。

```bash
source ~/.config/classshout/dev-env.sh
dotnet --info
pwsh --version
node --version
pnpm --version
java -version
adb devices
```

环境脚本配置 `DOTNET_ROOT`、`JAVA_HOME`、`ANDROID_HOME` 和工具路径。
文档站固定使用 pnpm 9.9.0；安装依赖时保留锁文件。

## 桌面构建与运行

```bash
cd ~/CodeX/ClassShout
dotnet build ClassShout.Linux.slnf -c Release
dotnet run --project src/ClassShout.Classroom -f net10.0 -c Release --no-build
dotnet run --project src/ClassShout.Teacher.Desktop -c Release --no-build
dotnet run --project src/ClassShout.RelayServer -c Release --no-build
```

Linux 解决方案排除 WinForms 安装器；联调与预览工具按宿主系统选择目标框架。
Windows 安装器、托盘交互和窗口唤回仍在 Windows 或原有 GitHub Actions 上验证。
完整桌面解决方案可交叉编译，但不等于 Windows 运行验收：

```bash
dotnet build ClassShout.DesktopOnly.slnf -c Release -p:EnableWindowsTargeting=true
```

```bash
bash scripts/regress-linux.sh
dotnet run --project tools/ClassShout.DesignPreview -c Release --no-build -- artifacts/preview
```

回归脚本启动独立状态目录的中继，结束时关闭测试进程并清理状态。
它不会宣称验证过 Windows 原生界面。

## 文档站

```bash
cd ~/CodeX/ClassShoutDocs
pnpm install --frozen-lockfile
pnpm run check
pnpm run docs:build
pnpm run docs:dev
```

本机依赖还原使用 `PUPPETEER_SKIP_DOWNLOAD=true` 跳过 PDF 工具的浏览器下载。
文档站开发、构建不受影响；首次导出 PDF 前需另装 Puppeteer 对应的 Chrome。

## Android

JDK 使用 17，SDK 位于 `~/Android/Sdk`，目标平台为 android-36。

```bash
dotnet workload install android
sdkmanager 'platform-tools' 'platforms;android-36' 'build-tools;36.0.0'
dotnet build src/ClassShout.Teacher.Android/ClassShout.Teacher.Android.csproj -c Release
adb devices
```

新版命令行工具会把 `sdkmanager` 转交给 Android CLI，也可逐包安装：

```bash
for package in platform-tools 'platforms;android-36' 'build-tools;36.0.0'; do
  android sdk install "$package"
done
```
首次安装 SDK 需要阅读并接受 Google 许可证。USB 调试需要手机授权，
加入 `adbusers` 后重新登录可使设备访问组生效。

正式 Android 签名继续使用原有 keystore 与 GitHub Secrets。
不要创建新密钥替代现有发布身份；Git 仓库不保存私钥。
ClassIsland 本体与其插件的 Windows 集成验收仍需 Windows 环境。

## 本机验证记录（2026-11-14）

- SDK 10.0.404、PowerShell 7.7.0、Node.js 24.22.3、pnpm 9.9.0。
- Linux Release 及启用 Windows targeting 的完整桌面构建：0 警告、0 错误。
- Linux 回归通过：局域网 481 项、中继 287 项，另含界面、网页与签名策略自检。
- 教师端、教室端均实际启动，已检查进程存活和落盘日志。
- 76 张 PNG 渲染自检通过；VuePress 完整构建通过，中英页面 39 对。
- Android Release 构建通过，0 错误、3 警告：Arch JDK 版本字符串触发 XA0033，
  SkiaSharp 两个 ABI 各触发一条既有 XA0141（16 KB 页兼容性）。没有隐藏这些警告。
- 当前未连接 Android 设备，未做真机验收；本机 APK 为开发签名，不能代替正式发布身份。

详细日志位于主仓库的 `artifacts/arch-environment/`，不纳入版本控制。

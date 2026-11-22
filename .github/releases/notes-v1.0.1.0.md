# ClassShout 1.0.1.0

- 新增 Windows/Linux ARM64 原生包、同架构更新包与 CI 启动验收。跨架构切换请手动选择安装包。
- Windows/Linux 桌面统一使用 FluentAvalonia，Linux 不再加载旧的 MD3 桌面样式；修正浅色主题下禁用录音按钮的图标对比度。
- 修复 Linux 默认字体不可用时的启动崩溃，首个窗口创建前即使用内置 HarmonyOS Sans SC。
- Windows 教师端、教室端和 Linux 解压版新增专用 `-update.zip`：下载校验、等待旧进程退出、替换、失败回滚和自动重启，并保留配置、铃声、日志与插件。
- Linux 教室端新增 AppImage、deb、rpm，保留可直接运行二进制的自包含 tar.gz。AppImage 下载新版整文件更新，deb/rpm 由系统包管理器更新。
- 修复修改镜像或代理后，更新检查器仍使用旧设置的问题。
- 增加更新路径穿越、符号链接、错误架构、文件篡改、回滚和真实进程退出等待的回归检查。
- 中英文 Linux 文档补充 Edge 在线语音和 Piper 1.4.2 离线神经语音的配置步骤；Piper 与模型不随软件捆绑。
- CI 分别在 Windows 和 Linux 打包，校验后合并附件；增加 Windows 更新包启动、Ubuntu deb 安装和 Fedora 容器 rpm 安装验收。

## 更新说明

没有专用更新功能的旧版需要手动安装本次版本；之后 Windows/解压版可在设置中选择「下载更新并重启」。更新包包含完整程序与运行时，不是差分补丁。更新期间请保持供电。

Android versionCode 已升至 22，继续使用原有固定签名。Android 构建仍存在已知的 Arch JDK 版本解析和 SkiaSharp 16 KB 页兼容性警告；本次不宣称解决这些问题。

本次标准附件共 28 个：Windows x64/ARM64 各含三个便携包、两个安装器和两个更新包；Linux x64/ARM64 各含教室端五种格式和中继服务器 tar.gz；另有通用 Android APK 与统一 SHA-256 清单。

本次按用户要求不再执行完整测试，仅进行打包和产物校验。完整回归与原生安装验收可在手动工作流中选择运行；ARM64 本次不宣称已通过真机验收。Windows 安装器仍使用现有 GUI。

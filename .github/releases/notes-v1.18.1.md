## 新功能

- **合并到 main 就自动打包并发布**：GitHub Actions 读 `Directory.Build.props` 里的版本号，标签 `v<版本>` 已经存在就什么都不做（版本没升 = 没有新东西要发），不存在就打包 —— Windows（教室端 / 教师端 / 服务器）、Linux（教室端 / 服务器）、Android APK —— 建发行版、传附件，并先跑一遍 `SHA256SUMS.txt` 核对。 · 本次提交 · 无

- **手动发布仍然可用**：`scripts/release.ps1` 那条路一行没动。CI 用的是同一个 `scripts/pack.ps1`，所以附件名、目录结构、校验清单一模一样 —— 两边不会分叉成"本地能发、CI 发出来少了点什么"。 · 本次提交 · 无

- **CI 上可以只打包不发布**：workflow_dispatch 里 `dry_run`（默认开）只跑打包、上传成 Actions 附件，用来验证流水线而不产生发行版。 · 本次提交 · 无

## Bug 修复

- **干净机器上打不出包**：依赖框架的发布物不会下载运行时包，而 `pack.ps1` 拼 `runtime\` 时正需要它 —— 本机缓存里有，CI 上第一次就挂在"找不到运行时包"。现在先对项目做一次 `-p:SelfContained=true` 的 restore 再拼运行时。 · 本次提交 · 无

- **runner 上的 zip 生成不出来**：原来用 `tar -a -c -f`（按扩展名选 zip），而 runner 的 PATH 里排前面的是 Git 附带的 GNU tar，它不认这个组合。Windows 的 zip 改用 `Compress-Archive`。 · 本次提交 · 无

- **runner 上的 .tar.gz 是残包**：106 MB 的服务器目录被压成 0.4 MB，`tar` 却仍然返回 0 —— 换 `System32\tar.exe` 也一样。现在 tar.gz 完全在进程里生成（`System.Formats.Tar` + `GZipStream`），不再依赖任何外部 tar；同时加了体积检查：压缩包不到源目录 5% 就直接失败，不让残包混进发行版。 · 本次提交 · 无

## 说明

- **平台覆盖**：Windows、Linux、Android 由这条流水线完整产出；**macOS** 只构建教师端（osx-arm64，未签名、未打 `.app`），作为 Actions 附件供有 Mac 的人试，不进发行版 —— 教室端在 macOS 上的播放与朗读还没有对应实现；**iOS** 目前没有头项目，要它得先加一个 Avalonia.iOS 头并配 Apple 证书与描述文件，那是移植工作而不是 CI 配置。
- 版本号**不会**被 CI 自动递增：版本是"这次要发什么"的决定，得由人写进 `Directory.Build.props`。忘了改就什么都不会发，这是有意的。
- 建议升级教室端与教师端（这一版主要是打包与自动化，应用行为没有变化）；服务器随包升级保持一致。
- 回归：局域网 **385 项**、中继 **199 项** 全部通过；CI 流水线本身在 runner 上跑了 6 轮才通过（前 5 轮分别暴露了上面三个问题）。

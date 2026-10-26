## 说明

先说结论：**这一版没有、也不可能有 Windows 7 支持**。不是没做，是做不到 —— 而且原因不在我们这边。

- 程序跑在 **.NET 10** 上。微软从 .NET 7 起就把 Windows 7 / 8.1 移出了支持列表（.NET 6 是最后一个列了 Windows 7 SP1 的版本，它自己也在 2024 年 11 月停止维护）。所以系统下限是 **Windows 10 1607 及以上**（含 Windows 11）。
- 在 Windows 7 上，.NET 的宿主在执行到我们的第一行代码之前就崩了，报的通常是一句「无法定位程序输入点」。没有哪个开关能把它打开。
- 也**不是改一行 TFM 的事**：界面用的 FluentAvalonia 2.5 **只发 net10.0** 的资产，要支持 Win7 就得换掉这个组件库、把目标降到 .NET Framework 4.8（最后一个能跑在 Win7 上的运行时）或已停止维护的 .NET 6，并重写依赖 .NET Core 专有 API 的部分（我们的 TLS/HTTP 层用了 `SocketsHttpHandler` + `SslClientAuthenticationOptions`，.NET Framework 上根本没有）。那是一条独立分支，不是一个构建选项。

那这一版做了什么：

- **在旧系统上把话说清楚**：Windows 启动脚本（`<程序名> 启动.cmd`）先查系统版本，低于 Windows 10 就打印一段说明并退出，而不是让宿主抛一句看不懂的崩溃。自检里用假的 `ver` 输出（6.1 = Windows 7）验过这条分支。
- **文档写清下限与原因**：[安装与部署](https://docs.wrd1145.top/guide/install.html) 新增「系统要求」与「为什么不支持 Windows 7」，[平台与功能支持矩阵](https://docs.wrd1145.top/reference/platforms.html) 的 Windows 列改成 `Windows 10 1607+（x64）`。
- 旧机器怎么办：装 Windows 10/11，或者换一台便宜的 x64 Linux 小主机 —— Linux 教室端包同样免装 .NET。

## Bug 修复

- **中文环境下双击「启动.cmd」会报一堆 `'…' is not recognized`，程序根本没起来**（1.17.0 起的 Windows 包都有这个问题）。两个原因叠在一起：启动脚本被统一压成了 LF 换行，而 cmd.exe 只在简单语句上容忍 LF，遇到标签、`for`、括号块就会错位；脚本里的中文注释在 GBK 代码页下被读成乱码。现在启动脚本是 **CRLF + 纯 ASCII**（说明文字挪进 `使用说明.txt`，并给它加 BOM，老记事本才不显示乱码）。这是我今天在真实解压目录里双击才发现的 —— 之前的自检只启动 exe，没走启动脚本。

## 说明

- 之前几个版本的安装包请换成这一版：1.17.0–1.19.0 的 Windows 包在中文系统上双击「启动.cmd」起不来（直接双击 exe 则会报 "You must install or update .NET"，因为没设 `DOTNET_ROOT`）。
- 回归：局域网 **391 项**、中继 **199 项** 全部通过；打包后在解压目录里实测两条分支（正常启动 → 进程起来并写日志；模拟 Windows 7 → 打印说明并退出）。

# ClassShout · 课堂喊话

教师用手机向教室电脑喊话：**文字由教室端的系统语音朗读，语音直接从教室音响播放**。
手机本身不发声 —— 声音一定从教室出来，这是「喊话」这个场景的关键。

- 运行时：.NET 10
- 界面框架：Avalonia 11.3.12 + FluentAvaloniaUI 2.5.1
- 视觉语言：Google Material Design 3（自建完整令牌与控件主题）
- 教室端：Windows 桌面应用，TTS 用系统 SAPI（`System.Speech`）
- 教师端：Android 应用（另有桌面头，用于在 PC 上调手机界面）
- 跨网络：可选的中继服务器（自带 WebUI 管理控制台），支持教师与教室不在同一网络

---

## 一、能做什么

| 能力 | 说明 |
|---|---|
| 文字喊话 | 手机输入 → 教室端用系统 TTS 朗读，可选语速/音量/语音，可打断上一条 |
| 语音喊话 | 手机按住说话 → 教室端实时播放（16 kHz 单声道 PCM，20 毫秒一片，低延迟） |
| 自动发现 | 同一 Wi-Fi 下自动列出所有教室端，无需记 IP；也支持手动填 `IP:端口` |
| **跨局域网** | 两端接到同一台中继服务器即可跨网络喊话，支持多班级并存互不串台 |
| **屏幕弹窗** | 教室端收到喊话时在屏幕边缘弹出提示卡，三档置顶强度可选，且不抢焦点 |
| **账号登录** | 老师用用户名或邮箱注册登录；教室端弹窗显示的是账号里的真实姓名 |
| **管理控制台** | 服务器自带 WebUI：查看教室与用户、重置口令、停用账号 |
| 常用语 | 「同学们请安静」等课堂高频用语一键填入 |
| 教室端控制 | 静音、立即停止、音量、语速、选择系统语音、亮/暗主题 |
| 状态同步 | 教室端的静音与音量实时回传给手机端显示 |

---

## 二、项目结构

```
ClassShout/
├─ ClassShout.slnx                     完整解决方案（含 Android）
├─ ClassShout.DesktopOnly.slnf         只含桌面项目，没装 Android SDK 时用这个
├─ docs/开发环境配置.md                 环境与 Android SDK 配置指引
├─ src/
│  ├─ ClassShout.Core/                 协议、网络、音频抽象（纯 .NET，不依赖 Avalonia）
│  │  ├─ Protocol/                     消息模型、JSON 编解码、TCP 分帧
│  │  ├─ Net/                          UDP 发现、教室端服务、教师端客户端
│  │  ├─ Remote/                       跨局域网中继：线路契约、服务器/教师端客户端、账号客户端、本地设置
│  │  └─ Audio/                        音频格式、电平计算、录音/播放/TTS 抽象
│  ├─ ClassShout.RelayServer/          中继服务器（ASP.NET Core）+ WebUI 管理控制台
│  │  ├─ ServerConfig.cs               服务器配置与内置管理员账号（首次启动生成随机口令）
│  │  ├─ UserStore.cs                  用户账号（口令以 PBKDF2 哈希保存）
│  │  ├─ ClassroomStore.cs             教室注册表
│  │  ├─ UserSessions.cs               登录会话令牌
│  │  ├─ RelaySessions.cs              教师端绑定会话
│  │  ├─ MessageHub.cs                 长轮询投递队列
│  │  └─ wwwroot/index.html            管理控制台页面（嵌入资源，无外部依赖）
│  ├─ ClassShout.Design/               Material Design 3 设计系统
│  │  ├─ Md3Theme.axaml                主题入口（应用只需引这一个文件）
│  │  ├─ Md3Typography.cs              平台字重适配（见「踩过的坑」）
│  │  ├─ Themes/Tokens/                颜色/字体/形状/高度/动效/状态层令牌
│  │  ├─ Themes/Controls/              控件主题（Button、TextField、Card、List…）
│  │  ├─ Themes/Md3Styles.axaml        作用于控件实例的全局样式
│  │  └─ Controls/                     自定义控件（Md3Icon、Md3Card、Md3AudioWave）
│  ├─ ClassShout.Teacher/              教师端共享 UI（两个平台头共用）
│  │  └─ Diagnostics/                  字体回退诊断页（真机排查用）
│  ├─ ClassShout.Teacher.Desktop/      教师端桌面头（手机比例窗口，用于调试）
│  ├─ ClassShout.Teacher.Android/      教师端 Android 头（AudioRecord 采集）
│  └─ ClassShout.Classroom/            教室端（Windows，TTS + 音频播放 + 屏幕弹窗）
│     ├─ Services/NotificationPresenter.cs  弹窗生命周期
│     ├─ Services/WindowTopmost.cs      Windows 置顶强度控制
│     └─ Views/NotificationWindow.axaml     弹窗本体
├─ assets/                             应用图标（由 IconGen 生成，勿手工编辑）
├─ scripts/pack.ps1                    打包脚本：exe + APK
└─ tools/
   ├─ ClassShout.DesignPreview/        把界面渲染成 PNG 并做像素级校验
   ├─ ClassShout.EndToEnd/             端到端联调自检（局域网 + 中继两套）
   ├─ ClassShout.IconGen/              程序化生成 Windows .ico 与 Android mipmap
   └─ AndroidSdkBootstrap/             仅用于触发 Android SDK 安装目标
```

### 分层原则

- **Core 不知道 Avalonia 的存在**。协议和网络可以单独测试、单独复用。
- **共享 UI 不知道平台的存在**。教师端的界面代码在 Android 和桌面上完全一样，
  唯一的差异点是启动时注册一个 `IAudioRecorder` 实现。
- **教室端不知道网络细节**。视图模型只管「收到文字就朗读、收到音频就播放」。

数据流：

```
教师端                                    教室端
┌──────────────┐                        ┌──────────────┐
│ MainView     │                        │ MainWindow   │
│  ↓ 命令      │                        │  ↑ 绑定      │
│ ShellViewModel│                       │ ClassroomVM  │
│  ↓           │                        │  ↑ 事件      │
│ ShoutChannel │──TCP 45900──────────→  │ ClassroomServer │
│  ↑           │                        │  ↓           │
│ IAudioRecorder│                       │ TTS / 播放器  │
└──────────────┘                        └──────────────┘
       ↑                                        ↑
       └─────UDP 45901 广播发现（教室端应答）─────┘
```

---

## 三、快速开始

### 教室端（Windows）

```powershell
dotnet run --project src\ClassShout.Classroom
```

启动后窗口顶栏会显示本机地址（例如 `192.168.1.5:45900`），把这个地址告诉教师端即可，
或者让教师端自动搜索。

### 教师端桌面头（在 PC 上调手机界面）

```powershell
dotnet run --project src\ClassShout.Teacher.Desktop
```

窗口按手机比例（430×900），跑的是和 Android 完全相同的界面。

### 教师端 Android

```powershell
dotnet build src\ClassShout.Teacher.Android -c Release
adb install -r src\ClassShout.Teacher.Android\bin\Release\net10.0-android36.0\com.classshout.teacher-Signed.apk
```

首次启动会申请麦克风权限；拒绝也不影响文字喊话。

### 没装 Android SDK 时

```powershell
dotnet build ClassShout.DesktopOnly.slnf
```

---

## 三之二、打包分发

```powershell
.\scripts\pack.ps1
```

一次产出可直接分发的文件：

```
dist/
├─ windows/
│  ├─ ClassShout.Classroom.exe          教室端，60.9 MB
│  └─ ClassShout.Teacher.Desktop.exe    教师端桌面头，47.2 MB
├─ android/
│  └─ classshout-teacher-1.0.0-universal.apk   63.3 MB（arm64 + x64）
└─ SHA256SUMS.txt
```

两个 exe 都是**自包含单文件**：拷到目标机双击即可，不需要预先安装 .NET 运行时 ——
教室电脑往往没有开发环境，这一点对实际部署很关键。

可选项：

| 开关 | 作用 |
|---|---|
| `-FrameworkDependent` | 改为依赖框架发布：教室端 60.9 → 34.0 MB，但目标机必须先装 .NET 10 运行时 |
| `-SplitApk` | 额外为 arm64 / x64 / arm 各出一个 APK（实测 32.6 / 33.3 / 32.0 MB） |
| `-SkipAndroid` | 跳过 Android 打包（本机未配 SDK 时用） |
| `-Configuration Debug` | 出调试包 |

> 依赖框架模式省不到一半：原生库（Skia、HarfBuzz）仍会打进单文件，
> 而单文件压缩在该模式下不被支持（会报 `NETSDK1176`）。
> 如果教室电脑没把握装运行时，直接用默认的自包含模式更省事。

> 单 ABI 打包用的是项目自定义的 `-p:AndroidAbi=android-arm64` 开关。
> 不能直接传 `RuntimeIdentifiers`：那是全局属性，会传播到被引用的 Core / Design / Teacher，
> 导致它们被要求按 Android RID 构建而编译失败。

### 应用图标

图标不是外挂素材，而是由 `tools\ClassShout.IconGen` 用代码生成的：

```powershell
dotnet run --project tools\ClassShout.IconGen -- .
```

- 图形取自 Material.Icons 的路径数据，配色取自 MD3 基线色板，与界面同一套设计语言
- 教师端：喇叭图形 + 主色 `#6750A4`；教室端：学校图形 + 次级色 `#625B71`（便于任务栏区分）
- 一次产出 Windows 的 `.ico`（16/24/32/48/64/128/256 七个尺寸）
  与 Android 的 mipmap（5 档密度 × 普通/圆形两种形状）
- 每个尺寸都独立渲染而非缩放，小尺寸下笔画不会糊

改配色或换图形只需改 `Program.cs` 里的两个常量，重新生成即可保持全部尺寸一致。

---

## 四、通信协议

教师端与教室端在同一局域网内直连，无需服务器。

### 端口

| 端口 | 协议 | 用途 |
|---|---|---|
| 45900 | TCP | 控制消息 + 音频流（同一条连接，靠帧类型区分） |
| 45901 | UDP | 教室发现（教师端广播探测，教室端单播应答） |

### TCP 分帧

```
┌──────────────┬──────────┬─────────────────────┐
│ 4 字节长度    │ 1 字节类型 │ 负载                 │
│ （大端，含类型）│ 1=控制    │ JSON 或 PCM          │
│              │ 2=音频    │                     │
└──────────────┴──────────┴─────────────────────┘
```

音频与控制共用一条连接：避免额外端口，也天然保证「先收到 audioStart 再收到音频数据」的顺序。

### 控制消息

JSON 编码，以 `type` 字段区分：

| type | 方向 | 说明 |
|---|---|---|
| `hello` | 教师 → 教室 | 握手，带协议版本与教师端名称 |
| `status` | 教室 → 教师 | 握手后自动回发，带教室名、静音、音量 |
| `textShout` | 教师 → 教室 | 文字喊话，带语速/音量/语音/是否打断 |
| `audioStart` | 教师 → 教室 | 语音开始，带采样率/声道/位深 |
| `audioEnd` | 教师 → 教室 | 语音结束 |
| `stop` | 教师 → 教室 | 立即停止朗读与播放 |
| `ack` / `error` | 双向 | 回执与错误 |
| `bye` | 教师 → 教室 | 主动断开 |

### 音频格式

16 kHz / 单声道 / 16 位 PCM，每片 20 毫秒（640 字节），约 32 KB/s。

没有引入 Opus 之类的编解码器：局域网上这个码率几乎不占带宽，而零依赖意味着
更低的延迟和更少的故障点。格式写在 `audioStart` 里，两端不强耦合 ——
Android 头若换用别的采样率，教室端会自动按收到的参数播放。

---

## 四之二、跨局域网（中继服务器）

老师和教室不在同一个网络时（老师在家、教室在学校），直连不可能建立。
这时让两端都去连一台双方都能访问到的服务器，由它转发。

### 为什么不是「真正的 Webhook」

Webhook 的标准含义是"服务器主动 POST 到你注册的 URL"。但教室端在 NAT 之后，
公网服务器**连不进去**，这个方向根本走不通。

所以投递改用 **HTTP 长轮询**：教室端发一个 GET 并保持住，服务器有喊话时才响应。
在教室端看来与收到 webhook 回调没有区别，而在任何网络环境下都能工作，
断线重连的语义也天然幂等（带上 `since` 游标即可续上，不需要自己实现心跳与重连状态机）。

### 部署服务器

```powershell
dotnet run --project src\ClassShout.RelayServer -- --urls "http://0.0.0.0:8080"
```

首次启动会在程序目录生成 `relay-config.json`，并往日志里打印管理员账号与随机口令：

```
──────────────────────────────────────────────────────────
  已生成管理员账号，请立即记录并妥善保存：
      账号：admin
      口令：LpseCk+kbL5Uy=YR^95v
  该口令同时保存在配置文件：.../relay-config.json
  登录后可在此修改口令，修改后配置里的值会同步更新。
──────────────────────────────────────────────────────────
```

控制台地址就是服务器地址本身：`http://<服务器>:8080/`。

生产部署建议放在 HTTPS 反向代理（Nginx / Caddy）之后，并把
`relay-config.json`、`relay-users.json`、`relay-state.json` 三个文件的权限收紧。

### 接入流程

**教室端**：右侧「跨局域网喊话」→ 填服务器地址 → 点「连接服务器」。
首次连接会向服务器上报本教室的**名字与 UUID** 并完成注册，界面上会显示
**UUID** 与 **口令**，两者都可一键复制。之后若在教室里改了名字，会自动同步到服务器。

**教师端**有两条绑定路径，任选其一：

| 路径 | 适合 | 做法 |
|---|---|---|
| 口令绑定 | 没有控制台、或临时用一下 | 「设备」页填服务器地址、教室 UUID、口令 → 「绑定教室」 |
| **分配绑定** | 学校统一管理 | 管理员在控制台上把班级指派给老师，老师在「管理员分配的班级」里**点一下即绑定** |

分配绑定的价值在于：口令一旦转发给老师就会扩散，而授权始终收在服务器上，
收回权限也只需要在控制台点一下「取消授权」——取消后已建立的连接会立即断开。

### 链路优先级：局域网 > 服务器

两条链路可以同时存在，优先级是**硬性**的：

```
局域网直连可用  →  一律走直连（延迟最低、不占公网带宽、不经过任何中间服务器）
直连不可用      →  自动回落到公网中继
两者都不可用    →  停止发送
```

直连断开时会自动检查中继是否还绑着，绑着就切过去；重新回到同网段后又自动切回直连。
教师端顶栏有一个常驻标签显示**当前实际在用的链路**，不必猜。

### 多班级

服务器以 UUID 区分教室，各教室的消息队列彼此独立 ——
一台服务器可以同时服务整个学校的班级，教师端绑到哪个教室就只收哪个教室的状态。

### 线路一览

| 方法 | 路径 | 用途 |
|---|---|---|
| `GET` | `/` | 管理控制台页面 |
| `POST` | `/api/auth/register` | 注册（用户名与邮箱至少一个） |
| `POST` | `/api/auth/login` | 登录（用户名或邮箱均可） |
| `GET` | `/api/auth/me` | 查询当前登录身份 |
| `POST` | `/api/classrooms/register` | 教室注册 / 带口令重连 |
| `GET` | `/api/classrooms/{uuid}` | 查询教室是否已注册 |
| `POST` | `/api/teachers/bind` | 教师绑定教室（UUID + 口令） |
| `POST` | `/api/teachers/{token}/text` | 发文字喊话 |
| `POST` | `/api/teachers/{token}/audio` | 发音频分片（裸二进制） |
| `GET` | `/api/teachers/{token}/events` | 教师端长轮询收教室状态 |
| `GET` | `/api/classrooms/{uuid}/events` | **教室端长轮询收喊话** |
| `POST` | `/api/classrooms/{uuid}/status` | 教室上报静音与音量 |
| `GET/DELETE/POST` | `/api/console/...` | 控制台接口（仅管理员） |

会话令牌走请求头（`X-Relay-Token` / `X-Auth-Token`）而不是查询串 ——
查询串会进访问日志，口令不该留在日志里。

---

## 四之三、账号与安全

### 两类账号，两套存储

| | 内置管理员 | 普通用户（老师） |
|---|---|---|
| 账号 | 固定 `admin` | 用户名和/或邮箱 |
| 口令存储 | **明文存在 `relay-config.json`** | PBKDF2-SHA256 + 随机盐，存在 `relay-users.json` |
| 为什么 | 运维必须能查出来，否则忘了就只能删文件重启重新生成 | 终端用户口令，服务器自己也不该读得出来 |
| 管理能力 | 进管理控制台、重置他人口令、停用账号 | 无 |

内置管理员的口令放在服务器本地的配置文件里，靠文件权限保护 ——
这是自建服务器运维账号在这个规模下的合理取舍，但它确实是一份明文凭据，
**不要把这台服务器的磁盘快照或配置文件外发**。

### 老师姓名不可冒充

教师端绑定教室时会把登录令牌一起带上，服务器**以账号里的姓名为准**，
忽略客户端传过来的字符串。这样教室端弹窗上显示的"哪位老师"才是可信的。

这条有测试覆盖：端到端用例故意在 `bind` 时传一个假名字，断言教室端收到的仍是注册姓名。

### 口令重置

管理员在控制台「用户」页点「重置口令」即可。重置后：

- 该账号的旧口令立即失效；
- 该账号**已签发的登录令牌同时作废**，需要重新登录 ——
  否则"改密码"就挡不住已经登录的会话。

---

## 四之四、屏幕弹窗

教室端收到喊话时，在屏幕边缘弹出一张小卡片，显示老师姓名、时间与喊话内容。

两个刻意的设计：

- **不抢焦点**（`ShowActivated=False` + `WS_EX_NOACTIVATE`）：老师正在投屏或操作时，
  提示不该把当前窗口顶下去；
- **不进 Alt+Tab**（`WS_EX_TOOLWINDOW`）：它是通知，不是一个应用窗口。

### 三档置顶强度

| 档位 | 行为 |
|---|---|
| 不置顶 | 普通窗口。别的窗口被激活后会被盖住 |
| 普通置顶 | 设置系统置顶。若别的程序也抢置顶，可能被压下去 |
| UIA 置顶（强制） | 在系统置顶之上周期性重申（每秒一次），能抢过多数置顶窗口 |

下图是实测：把教室端窗口移到弹窗所在位置，弹窗仍稳定压在最上层。

![弹窗覆盖在教室端窗口之上](docs/images/notification-topmost.png)

**关于"UIA 置顶"这个说法的边界**：Windows 存在"窗口段"机制，普通置顶窗口盖不住
更高窗口段的东西（开始菜单、任务管理器、通知中心等）。要盖住它们必须持有
**UIAccess 令牌**，而 Windows 要求 UIAccess 程序**必须数字签名且安装在安全目录**
（如 `Program Files`），否则清单里的 `uiAccess="true"` 会让程序直接无法启动。

因此未签名的便携版本做不到真正的 UIA 置顶，当前实现的是"周期性重申置顶"这一档。
如果后续要对 exe 签名并安装到安全目录，在 `app.manifest` 里加上
`<requestedExecutionLevel level="asInvoker" uiAccess="true"/>` 即可获得完整能力。

---

## 四之五、WebUI 管理控制台

服务器自己提供管理界面，登录与数据都走同一套通道 —— 不需要额外部署前端。

```
http://<服务器地址>:8080/
```

用内置管理员账号登录后有四页：

| 页面 | 内容 |
|---|---|
| 概览 | 已注册教室数、用户数、当前在线教师端数；初始口令未改时给出提醒 |
| 教室 | 教室名、UUID、在线教师数、注册与最后在线时间；可删除注册记录 |
| 用户 | 姓名、用户名、邮箱、状态、注册与最后登录时间；可重置口令、停用/启用 |
| **班级授权** | 已授权的「老师 ↔ 班级」配对；可取消授权（取消后连接立即断开） |
| 使用说明 | 教室端与教师端该怎么接进来的分步说明 |

「教室」页每一行都有 **授权给老师** 按钮，选中一位老师即可完成指派 ——
这就是老师端「管理员分配的班级」列表的数据来源。

几个实现上的选择：

- **页面作为嵌入资源编译进程序集**，不走 `wwwroot` 静态文件中间件。
  这样换工作目录、单文件发布都不会出现"页面 404"。
- **完全不引外部 CDN**。服务器可能部署在没有外网的内网里，
  页面一旦依赖外部资源就会整个白屏。所有 CSS 与 JS 都内联，配色直接用 MD3 令牌。
- **停止账号后立刻撤销其所有登录令牌**，否则已登录的客户端还能继续用。
  重置口令同理。

![WebUI 登录页](docs/images/webui-login.png)

![控制台概览](docs/images/webui-console.png)

![用户管理与重置口令](docs/images/webui-users.png)

---

## 五、Material Design 3 设计系统

### 应用接入方式

```xml
<Application.Styles>
  <fa:FluentAvaloniaTheme PreferSystemTheme="False" />
  <StyleInclude Source="avares://ClassShout.Design/Md3Theme.axaml" />
</Application.Styles>
```

顺序不能颠倒：Md3 主题在后才能覆盖 Fluent 的默认控件样式。
Fluent 底座负责 Md3 没有重做的控件（下拉框、滚动条、弹窗等），
这样不会出现「没被覆盖的控件长得格格不入」。

### 覆盖了哪些内容

- **颜色**：完整 MD3 色角色（48 项），亮/暗双主题，含 Surface 容器五级层次与 Fixed 色
- **字体**：15 种标准样式（Display/Headline/Title/Body/Label），以 `Classes="headlineSmall"` 使用
- **形状**：7 级圆角标度
- **高度**：5 级阴影
- **动效**：12 档时长 + 4 种缓动
- **状态层**：8% 悬停 / 10% 按下 / 38% 禁用内容 / 12% 禁用容器
- **控件**：Button（5 种变体 × 3 种尺寸）、TextBox（填充式/描边式，含浮动标签）、
  Card（4 种变体）、ListBoxItem、ProgressBar、Md3Icon、Md3AudioWave

另把 Avalonia 的 `SystemAccentColor` 对齐到 MD3 主色，因此滑块、开关、下拉框、
滚动条这些没有重做模板的控件也会呈现 MD3 配色，而不是系统默认的蓝色。

### 换主题色

不需要改设计系统。在应用的 `Application.Resources` 里用同名 key 覆盖即可：

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.ThemeDictionaries>
      <ResourceDictionary x:Key="Light">
        <SolidColorBrush x:Key="Md3.Primary" Color="#00696D" />
        <SolidColorBrush x:Key="Md3.OnPrimary" Color="#FFFFFF" />
        <SolidColorBrush x:Key="Md3.PrimaryContainer" Color="#6FF6FC" />
        <SolidColorBrush x:Key="Md3.OnPrimaryContainer" Color="#002021" />
      </ResourceDictionary>
      <ResourceDictionary x:Key="Dark"> <!-- 同理给一套暗色值 --> </ResourceDictionary>
    </ResourceDictionary.ThemeDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

应用层资源查找优先级更高，会自动生效。完整色角色清单见
`src/ClassShout.Design/Themes/Tokens/Color.axaml`。

### 踩过的坑：Android 上非 Normal 字重的中文会变方块

**现象**：教师端在 Android 真机上，部分中文显示成方块（tofu），而同一行里的
拉丁字母正常、其他中文也正常。

**定位过程**：在 Android 16 / x86_64 模拟器上做对照实验 ——
5 种字体链 × 4 档字重排成矩阵渲染，结论非常干净：

- **字体族完全无关**：不指定字体族、只写 `Noto Sans CJK SC`、只写 `sans-serif`、
  只写 `Roboto`，表现一模一样；
- **字重是决定因素**：`Normal` 下中文全部正常，`Medium` / `SemiBold` / `Bold`
  下中文全部变方块，而拉丁字母始终正常；
- 模拟器上系统只有 `NotoSansCJK-Regular.ttc` —— **只有 Regular 一档**。

所以根因是：Android 上 Avalonia 的逐字字形回退只覆盖 `Normal` 字重，
非 Normal 时解析到的字体没有中文字形，回退又不生效。换字体族解决不了。

修复前后（Android 16 模拟器实拍）：

| 修复前 | 修复后 |
|---|---|
| ![修复前](docs/images/android-font-before.png) | ![修复后](docs/images/android-font-after.png) |

**修复**：把 MD3 的强调字重做成资源 `Md3.Weight.Emphasis`，
Android 头在创建视图前把它覆盖为 `Normal`（见 `Md3Typography.ApplyPlatformDefaults`）。
Windows 保持 `Medium`，MD3 的层次感不受影响。

**顺带修掉的两个问题**：

1. `Md3.FontFamily` 原本挂在 `Style Selector="Window"` 上 ——
   而 Android 上根本没有 `Window`（用的是 `ISingleViewApplicationLifetime`），
   字体链在 Android 上从未生效过。改为 `TopLevel`（`Window` 与 Android 宿主视图的共同基类）。
2. 曾尝试用 `{OnPlatform Default=Medium, Android=Normal}` 做平台条件，
   但平台条件语法写在 `Setter` 的 `Value` 里只会得到一个字符串，
   不会转换成 `FontWeight`，运行时直接抛 `InvalidCastException` 把应用搞崩。
   资源 + 运行时覆盖才是可行路径。

排查工具保留在 `src/ClassShout.Teacher/Diagnostics/FontDiagnostics.axaml`：
把 `App.ShowFontDiagnostics` 改成 `true` 重新打包，应用启动后会直接进入字重矩阵页 ——
真机上一眼就能看出哪些组合可用，不必靠"改代码、打包、安装、截图"反复试。

---

## 六、验证

这个项目自带两套自检，改动后可以直接跑一遍确认没坏。

### 端到端联调

```powershell
# 局域网直连链路（19 项）
dotnet run --project tools\ClassShout.EndToEnd
dotnet run --project tools\ClassShout.EndToEnd -- --tts        # 真的用喇叭读一句中文

# 中继链路（22 项）—— 需要先自行启动中继服务器
dotnet run --project src\ClassShout.RelayServer -- --urls "http://127.0.0.1:8090"
dotnet run --project tools\ClassShout.EndToEnd -- --relay http://127.0.0.1:8090
```

跑的都是真实实现（不是 mock）。

**局域网链路（19 项）**：UDP 发现 → TCP 握手与状态回传 → 文字喊话 →
语音流逐字节校验（25600 字节逐片比对，确认无错位乱序）→ 停止指令 →
音频播放链路 → 系统 TTS → 断开清理。

**中继链路（22 项）**：教室注册（含新记录标记）→ 账号注册/登录/登出 →
错口令与不存在账号同样被拒 → 口令错误的绑定被拒 → UUID 不存在的绑定被拒 →
正确凭据绑定 → 文字喊话 → **喊话来源是账号里注册的姓名而非客户端自填** →
语音流逐字节校验 → 停止指令 → 反向状态通道 → 多班级隔离。

全部通过返回 0，可直接接进 CI。

### 界面渲染校验

```powershell
dotnet run --project tools\ClassShout.DesignPreview -- artifacts
```

把设计系统画廊和两个应用的真实界面渲染成 PNG，并做像素级自检：

- **内容判据**：不同颜色数 > 200（空白页只有一两种颜色）
- **布局判据**：占比最高的单色 < 85%（整片同一色说明内容没布局出来）
- **令牌判据**：各画面应当出现的 MD3 颜色确实出现在渲染结果中

这能自动发现「样式没加载」「令牌没解析」两类最常见故障，不需要人逐张看图。
产物在 `artifacts/`（生成物，不进版本控制）；下面这些是挑出来随文档走的：

![MD3 设计系统画廊](docs/images/design-system.png)

![教师端文字喊话页](docs/images/teacher-app.png)

![教室端界面](docs/images/classroom-app.png)

| 文件 | 内容 |
|---|---|
| `design-system-light.png` / `-dark.png` | MD3 组件画廊 |
| `teacher-light.png` / `-dark.png` | 教师端文字喊话页（手机比例） |
| `classroom-light.png` / `-dark.png` | 教室端待机界面 |

---

## 七、已知限制

1. **SkiaSharp 16 KB 页对齐告警**
   构建 Android 时会看到 `warning XA0141`：Avalonia 11.3.12 锁定的 SkiaSharp 2.88.9
   原生库未按 Android 16 要求的 16 KB 页对齐。
   本地开发和 4 KB 页设备（绝大多数手机）不受影响；
   若要上架 Google Play（2025 年 11 月起对 targetSdk 35+ 强制要求），
   需整体升级到 Avalonia 12.1.x + FluentAvalonia 3.1.0，那套用 SkiaSharp 3.119.x。

2. **APK 体积约 63 MB**（arm64 + x64 两个 ABI）
   体积大头是 `libassembly-store.so`（约 29 MB），即托管程序集被打包成原生库 —— 根源是
   关闭了裁剪以保证 Avalonia 的 XAML 反射解析不出问题。
   几个可选方向：
   - 只保留 `android-arm64`：约 32 MB（改 `RuntimeIdentifiers` 即可）
   - 改用 `AndroidLinkMode=SdkOnly`：只裁剪 BCL，保留 Avalonia 与业务程序集，
     风险可控，但**必须在真机上实测**后再上线
   - 发布到 Google Play 时用 AAB 格式，商店会按设备 ABI 自动拆分

3. **局域网链路的音频未加密**
   同网段直连是"有连接即接受"，没有配对码。校园内网可以接受，
   但如果教室端暴露在不可信网络里，应当改用中继链路（走 HTTPS）而不是开直连。

4. **中继链路本身不做端到端加密**
   服务器能看到文字内容与音频字节。它只做转发，不存储喊话内容，
   但"服务器可信"是这套设计的前提。要防止这一点需要端到端加密，当前没有实现。

5. **管理员口令以明文存在服务器配置文件里**
   这是为了让运维随时查得到（见「账号与安全」）。请收紧该文件权限，
   不要把服务器磁盘快照或配置文件外发。

6. **一对一喊话**
   教室端可同时接受多个教师端连接，但同一时刻只播放其中一路；
   没有实现多教师端的排队或抢麦机制。

7. **语音喊话上限 5 分钟**
   防止忘记松手一直录下去，到时会自动结束并发送。

8. **中继投递延迟约 100 毫秒起**
   教师端的音频在本地累积到 100 毫秒才发一趟 HTTP（每秒 50 个请求不可接受）。
   加上长轮询与网络往返，跨局域网语音的端到端延迟比局域网直连明显更高。
   这是"跨公网可用"换来的代价，同网段时走直连没有这个问题。

---

## 八、开发环境

见 [`docs/开发环境配置.md`](docs/开发环境配置.md)，其中包含实测过的
Android SDK 组件版本（`android-36` + `build-tools 36.0.0` + JDK 17）
与常见报错处理。

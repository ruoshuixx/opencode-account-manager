# OpenCode 账号管理（Avalonia 版）

管理本机 OpenCode 账号、额度和 API 供应商的桌面工具：切换账号、查询额度、导入导出账号文件。
**同一套 C# / Avalonia 代码，在 Windows 与 Ubuntu 桌面上运行**，界面完全一致。

- 技术栈：.NET 8 + Avalonia 11（Windows / Linux / macOS 桌面）。
- 许可证：[MIT](LICENSE)。
- 下载：[GitHub Releases](https://github.com/ruoshuixx/opencode-account-manager/releases/latest)
  （Windows 单文件 exe；Ubuntu 的 .deb 安装包）。

## 下载与安装

在 [Releases](https://github.com/ruoshuixx/opencode-account-manager/releases/latest) 下载，每个文件旁有同名 `.sha256` 校验文件。需要本机已安装 OpenCode。

- **Windows x64**：下载 `OpenCodeAccountManager-win-x64.exe`，放在可写目录，双击运行，无需安装 .NET。
  从旧版迁移：退出托盘里的旧工具，用新 exe 替换原目录里的旧程序，保留原目录的 `avalonia-settings.json` 与 `logs/`。
- **Ubuntu / Debian amd64**：下载 `opencode-account-manager_<版本>_amd64.deb`，然后
  `sudo apt install ./opencode-account-manager_<版本>_amd64.deb`。装好后在“应用程序”列表里可以找到。

账号导出文件含明文凭据，请自行妥善保管。检查更新只查询公开 GitHub Release，无需 GitHub 账号或令牌。

## 功能

- 程序内升级（Windows 单文件 exe、Ubuntu 的 .deb 安装版）：点“检查更新”，发现新版后确认一次，显示下载进度，校验 SHA-256 后自动重启到新版。启动时不会自动联网检查。

- 读取本机 OpenCode 凭据与供应商配置，一行一个账号的紧凑列表（深色界面）。
- Codex 行：**切号**（仅激活 OpenCode 凭据，不改 Codex CLI / Cockpit）、**查询额度**、**删除**。
- API 行：**配置**（供应商名称 / baseURL / 模型 / API 协议 / 可选轮换密钥）、**删除**（供应商定义 + 本地密钥凭据）。
- **添加账号**：两个选项卡 —— OpenAI OAuth 浏览器授权（自动回调 + 手动粘贴完整回调地址）、添加 API（供应商名称 / HTTPS URL / 模型 list / API Key）。
- 已有 API **供应商显示名称可修改**，内部 ID 和模型引用保持不变；没有本地凭据的配置行也可补填首个 Key。
- 添加 API 默认使用 Chat Completions，高级设置可选择 Responses；不自动探测或发送模型请求。新增、改名后的列表和导出读取当前磁盘配置；保存后另做只读后台协议确认，不将磁盘投影误当成运行时状态。
- 编辑 API 自动显示当前协议：勾选「使用 Responses API」调用 `/responses`，不勾选调用 `/chat/completions`；密钥留空时只更新配置，不更换凭据或供应商 ID。协议切换也会先加密备份，并做写入回读校验。
- Responses 使用 OpenCode V2 已内置的 `@opencode/ai/providers/openai/responses`，默认 HTTP，不强开 WebSocket。兼容读取旧 `openai-compatible/responses` 配置，保存时修复其供应商及已知模型级入口；正常文件监听生效，无需重启或调用 reload。
- 新增 / 编辑 / 导入 API 时，自动档位仅适用于 **Responses 协议 + 火山两种套餐 URL**：`https://ark.cn-beijing.volces.com/api/coding/v3`（Coding Plan）或 `https://ark.cn-beijing.volces.com/api/plan/v3`（Agent Plan），允许末尾 `/`；普通方舟 `/api/v3`、其他域名与代理地址均不生成。对方舟官方文档标注支持 `reasoning.effort` 的新模型，自动写入 `variants` 档位（如 `deepseek-v4.1-flash`/`deepseek-v4-pro` → low/high，`glm-5.3-flash` → low/high/max，`doubao-seed-2.1-*`/`doubao-seed-evolving` → low/medium/high），不含"关闭思考"档。**导入时，若与本地匹配供应商的套餐 URL、API Key 均相同且导入协议为 Responses，则按档位表更新已收录模型的 `variants`（会覆盖自定义档位），其他模型属性不因档位刷新而改变；未提供模型列表时刷新保留的本地模型。其他情况保留已有档位，未收录模型始终不猜测**。新增/编辑时仍只对新模型生成档位；档位表以方舟官方 Responses 深度思考文档为准（`ModelReasoningCatalog`），Chat 协议暂不生成。改变 URL 不会清除已有档位。
- 额度：只读 `GET https://chatgpt.com/backend-api/wham/usage`，显示 5 小时与本周期剩余进度条。
- 托盘常驻：点右上角 X **不退出**，而是隐藏到系统托盘（右下角）继续后台自动刷新；
  单击托盘图标或右键「显示主界面」恢复窗口，右键「退出」才真正结束。若托盘创建失败则 X 恢复为正常退出。
- 多选：每行两个勾选框 —— 左侧「额」用于额度查询，右侧「导」用于导出。
- 导入：解析 Cockpit 导出并用原版一致的安全流程写入（先备份、先建后删、永不激活其它账号）。
- 导出：Cockpit 兼容的**明文**账号数组，默认不覆盖，文件名 `opencode_yyyy-MM-dd_HH-mm-ss.json`。
- JSON 使用 Cockpit 原生的小写 `openai_api_key`，不再输出大写重复别名；补齐必需的 `created_at` / `last_used` 整数时间，保留已知历史时间，未知用 `0` 并警告。导入接受旧 Key 别名，但冲突值拒绝猜测；协议、名称、地址、模型与 Key 以当前配置为准，合法镜像中的备注、额度历史及能力设置保留。
- 自动刷新：启动时自动读取账号并查询一轮。「自动刷新当前」只查当前激活的 Codex 账号（列表勾选框
  只影响手动「刷新」）。设置里可勾「自动切换账号」：勾上后监控所有 Codex 账号，当前账号 5h 剩余
  <10%（周 <2%）时改为每 1 分钟查询；5h ≤5%（周 ≤1%）时自动切号——优先 5h 剩余 ≥25% 里重置
  最近的，没有就切有余额的，5h 全耗尽则切周限额（Pro/Business）账号；全部耗尽不切换，并恢复按
  设置间隔查询。10 分钟内不重复切换，每次切换写日志。
- 网络代理：本工具不再提供「系统代理」开关，也不再重启 OpenCode 或检测其代理状态。OAuth 授权与额度查询
  沿用系统默认的代理设置（Windows 系统代理、Linux 的 `http_proxy` 等环境变量）；OpenCode 自身是否走代理
  由 OpenCode / 代理软件（TUN 等）自行决定。
- 运行日志（已脱敏），可打开日志目录。

## 安全边界（与原版一致）

- 写入操作（添加 / 删除 / API 保存 / 导入）**先做加密备份**：Windows 用 DPAPI，Linux 用 AES-GCM 密钥文件（0600）。
- 删除要**精确 ID 校验并回读**，不能只隐藏；当前激活的 Codex 拒绝删除。
- 换密钥「先建新凭据、验证后再删旧凭据」，只补偿可确认属于本次的写入。
- 任何密钥都不写进界面、日志或返回值。
- 本地 Cockpit 镜像的认证信息及识别出的密码 / 2FA / Agent 私钥字段密封保存，明文仅用于用户明确选择的导出文件；旧别名在读取时从内存镜像移除，下一次缓存保存时迁移为加密字段。不自动修改用户原始备份 JSON。
- 切换账号只激活 OpenCode 凭据，不重启 OpenCode，不自动刷新登录。

## 浏览器授权添加 Codex

1. 点击「添加账号」→「OpenAI OAuth」→「在浏览器中打开」，在 OpenAI 官方页面登录并授权。
2. 正常情况下自动接收本机回调；如果没有自动完成，粘贴浏览器地址栏中的完整 `http://localhost:端口/auth/callback?code=...&state=...` 地址，点击「完成授权」。不要粘贴其它窗口或旧授权的回调。
3. 核对显示的邮箱和工作区，点击「保存账号」。重复身份须确认更新；先备份、创建并回读验证新凭据，再移除旧凭据。

授权使用 PKCE 与随机 state，仅监听 `127.0.0.1`（优先 1455，占用时回退 1457），10 分钟超时；关闭窗口或切到 API 选项卡会取消待完成授权并释放端口，不结束其它进程。令牌交换不跟随重定向，网络方式沿用设置中的代理开关及地址。密码只在官方浏览器页面输入，授权码、完整回调地址、令牌与 Key 不进入日志。

添加不会切换已有当前 OpenAI 账号；若本来没有当前 OpenAI 凭据，OpenCode 会按自身规则自动激活首个账号，并给出提示。添加窗口打开期间暂停本工具的自动刷新和自动切号；不会修改 Codex CLI 的 `auth.json` 或 Cockpit 数据。

## 构建与运行

需要 .NET 8 SDK（仅开发时需要；打包后的独立 exe 不需要）。

### 直接运行（开发）
```bash
dotnet run --project src/AccountManager.App
```

### 打包成单个独立 exe（推荐给最终用户）
Windows 上双击 `publish-windows.bat`，或执行：
```bash
dotnet publish src/AccountManager.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/win-x64
```
产物：`dist\win-x64\OpenCodeAccountManager.exe` —— 单个 exe，自带 .NET 运行时，**双击即可运行**。

发布到 GitHub 的流程见 [docs/RELEASING.md](docs/RELEASING.md)。不要上传整个 `dist` 目录。

Ubuntu：
```bash
dotnet publish src/AccountManager.App -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -o dist/linux-x64
```

Ubuntu 安装包（.deb，安装后在“应用程序”列表里能找到）：
```bash
scripts/Publish-Linux.sh 1.0.5 /tmp/ocam-publish
scripts/Build-Deb.sh /tmp/ocam-publish /tmp/ocam-deb      # 产出 opencode-account-manager_<版本>_amd64.deb
sudo apt install /tmp/ocam-deb/opencode-account-manager_*_amd64.deb
```
程序装在 `/opt/opencode-account-manager`，命令行可用 `opencode-account-manager` 启动；设置和日志写入
`~/.config/OpenCodeAccountImporter/`。卸载：`sudo apt remove opencode-account-manager`。
通过 .deb 安装的版本同样支持程序内升级：点“检查更新”→ 确认 → 显示下载进度 → 校验 SHA-256 → 程序退出，
弹出系统的管理员授权窗口（polkit）→ 自动安装新 .deb → 自动启动新版。授权被取消或安装失败时仍启动旧版，
下次启动会提示原因，日志在 `~/.config/OpenCodeAccountImporter/logs/update-*.log`。
发布时需要把 `opencode-account-manager_<版本>_amd64.deb` 和 `.deb.sha256` 上传到 Release 的资产里
（版本号与标签一致，例如 `v1.0.5` 对应 `opencode-account-manager_1.0.5_amd64.deb`，用
`scripts/Build-Deb.sh <发布目录> <输出目录> 1.0.5` 生成）。

> 注意：`run.bat` / `run.sh` 只是“一键启动”脚本（等同于你旧版的 `Start.bat`），
> 真正的程序是编译出来的 `.exe`（Windows）或无扩展名的可执行文件（Linux）。
> `Start.bat`（旧版）启动的是 PowerShell 脚本，所以旧版才必须是批处理；本版是真 exe。

## 项目结构

```
src/AccountManager.Core/     业务核心（无 UI 依赖）
  OpenCode/                  CLI 调用、HTTP API 包装、配置读写（含并发校验/原子替换）
  Accounts/                  凭据 → 账号映射、身份、激活、快照
  Quota/                     额度查询与解析、按账号选令牌
  Actions/                   添加、删除、API 供应商编辑
  Import/ Export/ Wake/      导入 / 导出 / 唤醒
  Security/                  密钥登记与脱敏、加密备份
  Json/ Logging/             JSONC 读取、深合并、日志
src/AccountManager.App/      Avalonia 界面（MVVM）
  Views/ ViewModels/ Converters/ Services/
```

## 跨平台说明

- `System.Security.Cryptography.ProtectedData`（DPAPI）仅 Windows 可用；Linux 自动切换为
  AES-GCM + 本机密钥文件方案（`~/.local/share/OpenCodeAccountImporter/`）。
- 配置目录通过 `opencode debug paths` 获取，Windows / Linux 均可。
- CLI 自动检测：Windows 查桌面版安装目录与 PATH；Linux 查 PATH 与 `~/.opencode/bin` 等。
- 界面只有一套 XAML（`App.axaml`、`Views/*.axaml`），两个平台共用，没有按平台分叉的样式。
- 字体：Windows 使用内置 Inter（中文由系统回退到微软雅黑）；Linux 使用已安装的系统字体（优先
  黑体 / 微软雅黑 / Noto Sans CJK SC / 文泉驿），可用环境变量 `OCAM_UI_FONT` 指定其他已安装字体族。
- 缩放：界面跟随系统缩放；屏幕的可用区域放不下设计最小尺寸（1000×560）时，整体按比例缩小，
  窗口最小尺寸同步缩小。GNOME 的“文本缩放”在 X11 下也会被当作整体缩放，需要固定倍数时可设置
  `AVALONIA_GLOBAL_SCALE_FACTOR`。

## 状态

账号列表、额度、切换、删除、API 编辑、导入导出及添加账号已接入。自动化测试覆盖 OAuth 自动/手动回调、PKCE/state、超时/取消、重复提交、API 新增/改名、并发保护、失败补偿及无真实账号的界面交互。

### 修复旧 Responses 配置

OpenCode **2.0.24** 的独立二进制未内置文档所列的 `@opencode/ai/providers/openai-compatible/responses` 入口，因而会报 `Cannot find package '@opencode/ai'`；这不是 Key 失效，重启也不能补齐该入口。

用新版工具读取账号，打开对应 API 的「配置」，保持 Responses 勾选、**Key 留空**并保存。无需删除账号、重填 Key 或安装 npm 包；工具保留 ID / 模型引用并改用内置 Responses 入口，随后确认后台已识别协议。未确认时会单独提示，不会把保存成功误报为模型已可用。

已在隔离的 2.0.24 私有服务及本地模拟接口中复现旧入口错误，并验证同一服务可从 Chat 切到 Responses、修复错误、再切回 Chat，全程未重启、未调用 reload、未使用真实凭据。供应商是否实际支持 Responses 仍需本人在 OpenCode 中验证。

重新导出后在 Cockpit 导入验证。若 Cockpit 中同一供应商已存在，它可能优先显示已有供应商的协议；此时检查 Cockpit 的供应商设置，而不是重填 Key 或删除账号。文件中的 `api_wire_api` 不会强行覆盖另一个应用的既有设置。

运行全部测试：`dotnet test AccountManager.sln -c Release`。浏览器 OAuth 与真实 API 供应商的端到端可用性仍需用户本人授权后验证；开发测试不会登录真实账号或写入真实 OpenCode 配置。

## 许可证

本项目以 [MIT 许可证](LICENSE) 发布。

# 发布流程

源码与发布都在本仓库（`ruoshuixx/opencode-account-manager`）。每个版本的 Release 带两组资产，
每个文件旁边都有同名的 `.sha256`：

| 平台 | 资产 | 程序内升级 |
|---|---|---|
| Windows x64 | `OpenCodeAccountManager-win-x64.exe` | 下载 → 校验 → 替换自身 → 重启 |
| Ubuntu / Debian amd64 | `opencode-account-manager_<版本>_amd64.deb` | 下载 → 校验 → 管理员授权（polkit）→ `apt` 安装 → 重启 |

标签 `v1.2.3` 对应程序版本 `1.2.3` 和 `.deb` 版本 `1.2.3`；资产名由程序在本地按标签推算，不读取远程给出的名字。

## 发新版本

1. 修改 `src/AccountManager.App/AccountManager.App.csproj` 的 `<Version>`，补 `docs/release-notes/v版本.md`。
2. 测试：`dotnet test AccountManager.sln -c Release`
3. 在 Windows 上生成 exe：`powershell -ExecutionPolicy Bypass -File scripts/Publish-Release.ps1`
   （也可以在 Linux 上交叉编译：`dotnet publish … -r win-x64 --self-contained -p:PublishSingleFile=true`）。
4. 在 Ubuntu 上生成 deb：
   ```bash
   scripts/Publish-Linux.sh 1.2.3 /tmp/ocam-publish
   scripts/Build-Deb.sh /tmp/ocam-publish /tmp/ocam-deb 1.2.3
   ```
5. 创建 Release 并上传四个文件（exe、deb 及各自的 `.sha256`）：
   ```bash
   gh release create v1.2.3 --title "OpenCode Account Manager v1.2.3" --notes-file docs/release-notes/v1.2.3.md \
       OpenCodeAccountManager-win-x64.exe OpenCodeAccountManager-win-x64.exe.sha256 \
       opencode-account-manager_1.2.3_amd64.deb opencode-account-manager_1.2.3_amd64.deb.sha256
   ```
   GitHub 会自动附带该标签的 Source code 压缩包。不要使用预发布标记（程序只认正式版）。

不要打包运行过的程序目录，它可能含个人设置和日志。发布资产只放编译产物和校验文件。

## 自动构建（可选）

`docs/ci/release.yml` 是一份现成的 GitHub Actions 工作流：推送 `v*` 标签（或手动触发）时测试并构建 exe 和 deb，
保存为 Actions 产物；如果该标签的 Release 里还没有对应资产，就补传缺失的文件，已有的资产不会被覆盖。

启用方法：把它复制到 `.github/workflows/release.yml` 再提交。推送工作流文件需要带 `workflow` 权限的令牌
（`gh auth refresh -s workflow`）。

## 程序中的更新流程

更新检查只在主界面或托盘手动触发，启动时不联网检查。发现新版后确认一次，显示下载百分比和 MB 进度。

- 当匿名 API 返回 403 / 429 时，改从公开 Release 页面的 latest 重定向识别稳定版本。
- 程序核对发布地址、文件长度和 SHA-256；Windows 还校验 PE 类型，deb 校验包头。
- 失败时停止更新并保留旧版本；Windows 尽力恢复旧 exe，deb 在授权被取消或安装失败时重新启动旧版并在下次启动提示原因。
- 开发运行和不满足条件的安装方式只提供下载页面。

### 各平台的实现约束

- Windows：单文件 exe 把自己改名为同目录唯一的 `.old-<随机值>.exe`，放入新版并启动，新版握手成功后清理旧文件。目录不可写时提示错误，不自动提权。
- Ubuntu .deb：程序在 root 所有的 `/opt/opencode-account-manager`，不能替换自身。下载完成后程序退出，
  分离的脚本 `apply-update.sh`（用户身份）通过 `pkexec` 调用 root 辅助脚本 `install-update`；后者重新计算安装包的 SHA-256，
  核对包名、架构，并要求版本比已安装的新，再用 `apt-get install` 安装，最后启动程序。
- Ubuntu 便携版（`OpenCodeAccountManager-linux-x64` 单文件，装在用户可写目录）：沿用与 Windows 相同的“替换自身”流程。

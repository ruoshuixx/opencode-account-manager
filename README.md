# OpenCode 账号管理

Windows x64 便携式账号管理工具，基于 .NET 8 / Avalonia。

## 下载

**[下载最新版 EXE](https://github.com/ruoshuixx/opencode-account-manager/releases/latest/download/OpenCodeAccountManager-win-x64.exe)** · [版本说明与校验文件](https://github.com/ruoshuixx/opencode-account-manager/releases/latest)

无需安装 .NET。将 EXE 放在可写目录，双击运行；需要本机已安装 OpenCode。

首次从旧版迁移：退出托盘里的旧工具，将新 EXE 放到原目录，可改名为 `OpenCodeAccountManager.exe` 替换旧程序。保留原目录的 `avalonia-settings.json` 与 `logs/`。

## 功能

- OpenCode 账号读取、添加、导入导出、额度查询与账号切换。
- 自动刷新当前账号、可选低额度自动切换。
- 仅为 OpenCode 注入系统代理，支持代理状态检测与修复。
- 点窗口 X 隐藏到系统托盘，托盘菜单可恢复或退出。
- 启动静默检查更新；点击“升级并重启”确认后，自动下载并显示进度，校验成功后重启到新版。主界面与托盘可手动检查，设置可关闭启动检查。

## 发布与隐私

本仓库只发布程序、SHA256 校验文件及使用说明，源码在独立私有仓库维护。GitHub 自动显示的 “Source code” 压缩包只包含此仓库的说明。

发布包不包含个人账号、令牌、配置、日志或备份。程序运行后读取本机 OpenCode 数据，个人设置与日志在运行时保存。检查更新只查询公开 GitHub Release，无需 GitHub 账号或令牌；升级只替换程序文件。

账号导出文件可能含明文凭据，请自行妥善保管。不要将运行过的整个程序目录作为软件发布包分享。

## 校验

在 PowerShell 中执行：

```powershell
Get-FileHash .\OpenCodeAccountManager-win-x64.exe -Algorithm SHA256
```

结果应与同一 Release 中 `.sha256` 文件的第一段一致。内置升级会自动完成校验。

---
name: axial-settings-secrets
description: Manages AxialSqlTools settings persistence and secrets (unified Roaming config folder, DPAPI, Windows Credential Manager). Use when adding settings fields, Settings window UI, encrypting passwords/API keys, migrating old registry/LocalAppData paths, or changing SavedConnectionStore/ProfileStore credential handling.
---

# 设置与凭据

## 统一配置根目录（必遵）

所有用户级个性配置统一放在：

`%APPDATA%\AxialSqlTools\`  
（`C:\Users\<用户>\AppData\Roaming\AxialSqlTools\`）

| 内容 | 文件/目录 |
|---|---|
| 通用设置（界面语言等） | `settings.json` |
| 代码片段 | `snippets.json` |
| Data Transfer 连接 | `data-transfer-connections.json` |
| GitHub Sync 配置 | `github-sync-profiles.json` |
| Query History（文件模式） | `query-history\` |
| 查询模板默认目录 | `templates\` |
| 日志 | `logs\` |

共享说明见 `_shared/conventions.md`「用户配置目录」。

**不要**再往下列位置写新配置：

- `HKCU\AxialSqlTools\Settings`（旧）
- `%LOCALAPPDATA%\AxialSQL\`（旧，且目录名不一致）
- `文档\AxialSqlToolsTemplates`（旧默认模板目录）

迁移旧数据时：读旧位置 → 写入统一目录 → 必要时保留只读兼容。

## 敏感信息例外

| 类型 | 机制 |
|---|---|
| 密码 / 连接串 / API Key | DPAPI（CurrentUser）后写入上述 JSON |
| GitHub Token 等 | `Modules/WindowsCredentialHelper.cs`（凭据管理器） |

## UI

本仓库**未**使用 `ProvideOptionPage`；UI 在 `WindowSettings/SettingsWindow*.cs` / `*Control.xaml(.cs)`。

## 添加设置项

1. 写入 `%APPDATA%\AxialSqlTools\` 下约定文件（优先 `settings.json` 或对应 Store）。
2. 敏感值：加密存储，UI 用 PasswordBox/占位，勿日志打印明文。
3. Settings 窗口绑定控件与加载/保存逻辑。
4. 功能侧只通过 `SettingsManager`（或对应 Store）读取。

## 安全检查清单

- 新密码/连接串/API Key 是否加密？
- JSON 序列化是否忽略应保密字段？
- 异常路径是否把密钥写进 NLog？

## 验证

保存→重启 SSMS→值仍在；改密码后旧值不可读；导出/分享设置文件不含明文秘密。

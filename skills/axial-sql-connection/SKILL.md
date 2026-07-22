---
name: axial-sql-connection
description: Obtains and builds SQL connections from the active SSMS editor or Object Explorer in AxialSqlTools, including Windows/SQL/Entra auth and encrypt options. Use when a feature needs the current server/database, connection string, SqlConnection, or authentication/Encrypt/TrustServerCertificate behavior.
---

# SQL 连接与认证

## 入口

优先使用 `Modules/ScriptFactoryAccess.cs`，不要从 DTE 自己拼一套。

典型能力：

- 当前查询窗口连接信息（服务器、库、认证、Encrypt 等）
- Object Explorer 选中节点相关连接
- 当前编辑器文本/选区（若方法已提供）

## 使用方式

1. 从 `ScriptFactoryAccess` 取连接参数。
2. 用 `Microsoft.Data.SqlClient` 创建 `SqlConnection`（与仓库其余代码一致）。
3. UI 功能：连接失败要可提示；后台功能：尊重取消。

## 认证注意

- Windows / SQL 登录 / Microsoft Entra（Active Directory）路径都可能出现——改动时对照现有分支，勿只测 SQL 账号。
- 密码与连接串落盘必须加密；见 `axial-settings-secrets`。

## 相关功能

- Health / Quick Search / Data Transfer / Sync GitHub / Script Object 均依赖此层
- 连接颜色等 UI：逻辑在 `AxialSqlToolsPackage` 与设置中的颜色规则

## 验证

分别用 Windows 认证与 SQL 认证各连一次，确认新功能拿到的 Database 与 SSMS 状态栏一致。

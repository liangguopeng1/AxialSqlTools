# AxialSqlTools Agent Skills

项目根目录 skills，供 Cursor Agent 在开发本仓库时按场景加载。共享背景见 `_shared/`。

## 选用指南

| 你在做什么 | 使用 skill |
|---|---|
| 不熟悉仓库 / 找入口 | `axial-overview` |
| 加菜单命令、快捷键 | `axial-add-command` |
| 加 Tool Window / WPF UI | `axial-tool-window` |
| 网格/状态栏/查询事件坏了、SSMS 升级 | `axial-ssms-compat` |
| 取当前连接、认证、Encrypt | `axial-sql-connection` |
| T-SQL 格式化 / ScriptDOM | `axial-scriptdom` |
| 网格导出 Excel/Sheets/Email/INSERT | `axial-grid-export` |
| Quick Search | `axial-quick-search` |
| Health Dashboard / DMV | `axial-health-dashboard` |
| Query History / Statistics Summary | `axial-query-history` |
| Data Transfer / BULK | `axial-data-transfer` |
| Sync to GitHub / DacFx / SMO 脚本 | `axial-sync-github` |
| Query Templates / Snippets | `axial-snippets-templates` |
| 设置窗口、注册表、凭据加密 | `axial-settings-secrets` |
| SQL IntelliSense 补全/ToolTip/参数提示 | `axial-intellisense` |
| 构建、安装、改版本、发 ZIP | `axial-build-release` |

## 共享参考

- `_shared/architecture.md` — 分层与关键类
- `_shared/conventions.md` — UI 线程、csproj、日志、安全约定

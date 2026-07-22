# AxialSqlTools Skills 设计

日期：2026-07-22  
状态：已批准

## 目标

在项目根目录 `skills/` 下建立全覆盖 Agent Skills，使后续开发/排障时能按场景自动加载仓库特有知识（SSMS VSIX、反射适配、SMO/DacFx、构建安装等）。

## 约束

- 落盘：`skills/`（根目录），不是 `.cursor/skills/`
- 语言：中文说明 + 英文标识符原文
- 组织：细粒度多 skill + `_shared/` 共享参考
- 不包含 eval 套件（后续可加）

## 目录结构

```text
skills/
├── README.md
├── _shared/
│   ├── architecture.md
│   └── conventions.md
├── axial-overview/
├── axial-add-command/
├── axial-tool-window/
├── axial-ssms-compat/
├── axial-sql-connection/
├── axial-scriptdom/
├── axial-grid-export/
├── axial-quick-search/
├── axial-health-dashboard/
├── axial-query-history/
├── axial-data-transfer/
├── axial-sync-github/
├── axial-snippets-templates/
├── axial-settings-secrets/
└── axial-build-release/
```

每个 skill：`SKILL.md`（frontmatter + 步骤）+ 按需 `references/`。

## Skill 清单与触发意图

| Skill | 用途 |
|---|---|
| axial-overview | 架构地图、入口、找文件 |
| axial-add-command | VSCT / MenuCommand / Package 注册 |
| axial-tool-window | ToolWindow + WPF 三件套 |
| axial-ssms-compat | GridAccess / 反射 / SSMS 升级兼容 |
| axial-sql-connection | 当前连接、认证、Encrypt |
| axial-scriptdom | T-SQL 格式化 |
| axial-grid-export | 网格导出与复制 |
| axial-quick-search | 跨库对象搜索 |
| axial-health-dashboard | 健康面板 / DMV / 图表 |
| axial-query-history | 查询历史 + Statistics Summary |
| axial-data-transfer | BULK 跨库传输 |
| axial-sync-github | DacFx/SMO + GitHub 同步 |
| axial-snippets-templates | 模板菜单 / Snippet |
| axial-settings-secrets | 注册表 / DPAPI / 凭据 |
| axial-build-release | 构建、重装、版本、ZIP |

## 写作规范

- `description`：第三人称，含 WHAT + WHEN，便于触发
- 省略 `disable-model-invocation`，允许按上下文自动选用
- SKILL.md 保持精简；细节放 `_shared/` 或 skill 内 `references/`
- 复用现有代码模式；标注风险（私有 API、绝对路径引用）
- 不删除既有代码注释；生成代码时避免无意义空行

## 非目标

- 不重构应用代码
- 不引入 CI / 测试工程（除非另开任务）
- 不强制 commit（由用户显式要求）

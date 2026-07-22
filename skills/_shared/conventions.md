# AxialSqlTools 开发约定

## UI 线程

- 初始化命令、操作 DTE/工具窗口前：`await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken)`
- 后台查询后回 UI：用 Package/`JoinableTaskFactory`，不要直接跨线程碰控件

## 添加源文件（必做）

传统 csproj **不会**自动包含新文件。新增 `.cs` / `.xaml` 后必须在 `AxialSqlTools.csproj` 登记：

- Compile：`<Compile Include="Path\File.cs" />`
- WPF Page：`<Page Include="Path\File.xaml" ... />`（对照现有条目）

## 命令 ID

- Command Set GUID：`45457e02-6dec-4a4d-ab22-c9ee126d23c5`（与现有命令一致）
- 新 `CommandId`：在 `AxialSqlToolsPackage.vsct` 的 `IDSymbol` 中分配未占用整数，并在命令类中使用同一常量

## 复用优先

| 需求 | 复用 |
|---|---|
| 当前连接/编辑器文本 | `ScriptFactoryAccess` |
| 结果网格 DataTable / 对齐 / 状态栏 | `GridAccess` |
| 设置读写 | `SettingsManager` |
| 工具窗口主题 | `Modules/ToolWindowThemeSupport.cs`、`Themes/` |

## 异常与日志

- 避免空 `catch { }` 吞掉新代码的失败；至少 `_logger` 或现有日志点
- SSMS 私有字段访问失败应可降级，不要拖垮整个 Package 加载

## 代码风格（仓库偏好）

- 不要加无意义空行
- 复用已有代码时保留原有注释
- 改动范围只覆盖任务需要的文件

## 用户配置目录（统一）

所有用户级配置、片段、连接档案等，统一放在：

`%APPDATA%\AxialSqlTools\`  
（即 `C:\Users\<用户>\AppData\Roaming\AxialSqlTools\`）

约定布局（新增/迁移时遵守，不要再散落到注册表或其它 AppData 目录）：

| 内容 | 路径 |
|---|---|
| 通用设置（含界面语言等） | `%APPDATA%\AxialSqlTools\settings.json` |
| 代码片段 | `%APPDATA%\AxialSqlTools\snippets.json` |
| Data Transfer 连接 | `%APPDATA%\AxialSqlTools\data-transfer-connections.json` |
| GitHub Sync 配置 | `%APPDATA%\AxialSqlTools\github-sync-profiles.json` |
| Query History（文件模式） | `%APPDATA%\AxialSqlTools\query-history\` |
| 查询模板（默认目录） | `%APPDATA%\AxialSqlTools\templates\` |
| 日志 | `%APPDATA%\AxialSqlTools\logs\` |

**例外（非文件配置）：**

- 敏感 Token（如 GitHub）：`WindowsCredentialHelper` / Windows 凭据管理器
- 密码类字段：DPAPI 加密后写入上述 JSON（勿明文）

**历史位置（仅兼容/迁移，新代码勿再写入）：**

- `HKCU\AxialSqlTools\Settings`
- `%LOCALAPPDATA%\AxialSQL\`
- `文档\AxialSqlToolsTemplates`

读写入口优先集中在 `SettingsManager` 或专用 Store；路径用 `Environment.SpecialFolder.ApplicationData` + `"AxialSqlTools"`，不要硬编码用户名。

## 敏感信息

- SMTP / API Key / 连接串密码：走 DPAPI（见 `SettingsManager` / `SavedConnectionStore`）
- GitHub Token：优先 `WindowsCredentialHelper`，审查是否误写入明文 JSON

## 本地验证

1. 关闭 SSMS
2. Release 构建或 `.vscode/axial-extension.ps1`
3. 重启 SSMS，检查工具栏/菜单/目标窗口

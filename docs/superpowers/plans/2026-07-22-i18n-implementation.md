# 国际化 Implementation Plan

> **For agentic workers:** Implement task-by-task. Checkboxes track progress.

**Goal:** 默认中文 UI，Settings 可选英文；配置统一到 `%APPDATA%\AxialSqlTools\`。

**Architecture:** `settings.json` + `Strings(.en).resx` + 启动设 Culture；菜单用 CommandBar 文案覆盖；设置窗先本地化。

**Tech Stack:** .NET Framework 4.7.2, WPF, VSIX, Newtonsoft.Json, resx

## Global Constraints

- 配置根：`%APPDATA%\AxialSqlTools\`
- 默认语言：`zh-CN`
- 不把 `uiLanguage` 写入注册表
- 不写无意义空行；保留原有注释

---

### Task 1: 配置路径与 settings.json

- [x] `UserConfigPaths.cs` / `UiSettingsStore.cs` / `UiCultureService.cs`
- [x] 接入 Package 启动；日志改到 `logs\`

### Task 2: 字符串资源与菜单

- [x] `Strings.resx` + `Strings.en.resx` + Designer
- [x] `MenuTextLocalizer` 覆盖工具栏/菜单

### Task 3: Settings UI

- [x] 常规 Tab + 语言下拉
- [x] 设置窗主要标题/Tab 本地化

### Task 4: 路径统一（与约定一致）

- [x] 模板默认目录、QueryHistory、DataTransfer、GitHub profile、日志指向统一根目录

### Task 5: 验证

- [x] Release 构建通过
- [ ] 手动：默认中文；改英文保存；重启后菜单英文

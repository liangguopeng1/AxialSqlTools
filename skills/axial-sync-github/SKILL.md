---
name: axial-sync-github
description: Maintains AxialSqlTools Sync-to-GitHub database scripting via DacFx and SMO plus Octokit GitHub commits. Use when changing DatabaseScripter, DacServices.Extract, SMO ScriptingOptions, GitHub trees/commits, sync profiles, or agent/login scripting.
---

# Sync to GitHub（DacFx / SMO）

## 关键文件

- `SyncToGitHub/DatabaseScripterToolWindow*.cs` / `*Control.xaml(.cs)`
- `SyncToGitHub/GitRepo.cs`、`GitHubSyncProfile.cs`、`ProfileStore.cs`
- NuGet/程序集：DacFx、SMO、`Octokit`

## 流程（概念）

1. 读 profile / 服务器与数据库列表  
2. DacFx `DacServices.Extract` 和/或 SMO `Scripter` 生成 SQL  
3. 算 blob SHA，与远程对比 → 增删改  
4. 可选确认 → GitHub Git Data API 提交  

## 修改指引

1. **库对象脚本**：DacFx 路径；**服务器级/Agent/Login/Linked Server**：SMO 路径——不要混用职责。
2. 权限与 Login 脚本注意脱敏；对照现有 TODO/注释。
3. Token：优先 `WindowsCredentialHelper`；审查 `GitHubSyncProfile` / JSON 是否误存明文 Token。
4. 大库：保持临时目录清理与进度反馈；失败可重试单步。
5. Profile 存储：`%LOCALAPPDATA%\AxialSQL\github-sync-profiles.json`（以 `ProfileStore` 为准）。

## 验证

小库 dry-run 差异列表正确；提交后 GitHub 文件与本地脚本一致；取消/失败不留下半提交状态（或可解释）。

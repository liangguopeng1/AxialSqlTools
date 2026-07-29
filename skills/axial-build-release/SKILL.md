---

name: axial-build-release

description: Builds, reinstalls, versions, and packages the AxialSqlTools SSMS 22 VSIX using MSBuild, VSSDK, and pack-release.ps1 / .vscode/axial-extension.ps1. Use when compiling the extension, one-click packaging, fixing build references to SSMS install paths, changing VSIX version, creating release ZIP, or reinstalling into SSMS.

---



# 构建与发布



## 前置



- 已安装 **SSMS 22**（`.csproj` 默认引用 `C:\Program Files\...`；若装在 `D:\` 等路径，一键打包脚本会临时改写 HintPath）

- 已安装 **Visual Studio（含 VSSDK）** 或 **VS Build Tools（含 MSBuild）**

- 重装前**关闭 SSMS**



## 常用路径



- 工程：`AxialSqlTools/AxialSqlTools.csproj` / `.sln`

- Manifest 版本：`AxialSqlTools/source.extension.vsixmanifest`（当前 Identity Version）

- 一键打包：`skills/axial-build-release/scripts/pack-release.ps1`

- 日常构建/重装：`.vscode/axial-extension.ps1`、`.vscode/tasks.json`

- 输出 VSIX：`AxialSqlTools/bin/<Config>/AxialSqlTools.vsix`

- 输出 ZIP：`AxialSqlTools/bin/<Config>/AxialSqlTools_SSMS22_<version>.zip`

- Debug 本地扩展目录（csproj 可能复制到）：  

  `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\AxialSqlTools\`



## 一键打包（推荐发版）



在仓库根目录执行：



```powershell

.\skills\axial-build-release\scripts\pack-release.ps1

```



或：



```powershell

powershell -NoProfile -ExecutionPolicy Bypass -File .\skills\axial-build-release\scripts\pack-release.ps1

```



脚本会：



1. 自动找 MSBuild（优先带 VSSDK 的 VS，否则 BuildTools）

2. 自动找 SSMS 22（`C:\` / `D:\`），必要时临时改写 csproj HintPath

3. BuildTools 场景下临时去掉 EnvDTE 引用，避免与 `Microsoft.VisualStudio.Interop` 冲突

4. Restore + Release 构建 VSIX

5. 在 `AxialSqlTools/bin/<Config>/` 生成 `AxialSqlTools_SSMS22_<version>.zip`（与 VSIX 同目录）

6. **始终还原** csproj（`try/finally`）



可选参数：`-Configuration Release`、`-Platform AnyCPU`、`-SkipRestore`



## 日常开发一键重载（推荐）



改完代码后无需手动卸载 VSIX，在仓库根目录执行：



```powershell

.\tools\dev-reload.ps1

```



脚本会：自动探测 C/D 盘 SSMS 22 → Release 构建 → 关闭 SSMS → 静默覆盖安装 → 启动 SSMS。



可选参数：`-Configuration Debug`（更快、带符号）、`-SkipBuild`、`-NoLaunch`



在 Cursor/VS Code 中也可运行任务：**AxialSqlTools: Dev reload (build + install + SSMS)**。



## 构建（仅编译）



```powershell

.vscode\axial-extension.ps1 -Action Build -Configuration Release -Platform "Any CPU"

```



说明：该脚本要求本机有 **VSSDK 组件**，且 SSMS 路径需与 csproj 一致；本机环境不齐时请改用上面的一键打包脚本。



## 重装到 SSMS



```powershell

.vscode\axial-extension.ps1 -Action Reinstall -Configuration Release -Platform "Any CPU"

```



脚本会关闭 SSMS、用 SSMS 自带 `VSIXInstaller.exe` 静默卸装/安装。



## 发版本



1. 改 `source.extension.vsixmanifest` 的 `Version`

2. 跑一键打包脚本

3. 在干净 SSMS 上验证工具栏与关键功能



## 注意



- Manifest 目标为 **amd64**；Solution 里 x86/arm64 配置勿盲目假定可用

- 无 CI：本地机器路径与 SSMS 安装位置是构建成功的关键

- 引用缺失时先确认 SSMS 安装路径；一键打包脚本会尝试自动 remap

- `.vscode/axial-extension.ps1` 的 VSIXInstaller 路径**硬编码 C 盘**；SSMS 装在 D 盘时 Reinstall 会失败，需改用 D 盘路径手动执行 `/quiet /uninstall:AxialSqlTools` + `/quiet <vsix>`（pack-release.ps1 无此问题，会自动探测 C/D 盘）

- 本机（RTSD）环境：SSMS 22 在 `D:\Program Files\...`；只有 VS2022 **BuildTools**（无 VSSDK 组件），日常 Build/Reinstall 脚本不可用，统一用 `pack-release.ps1`



## 验证



SSMS 关于/扩展列表中版本正确；Axial SQL Tools 工具栏出现；抽测一条命令与一个工具窗口。



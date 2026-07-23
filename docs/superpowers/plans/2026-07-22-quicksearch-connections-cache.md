# Quick Search Connections + Cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (inline in this session).

**Goal:** Quick Search 可选 OE 已连接服务器/数据库，修复 Script SSL，并行搜索 + 可选本地索引。

**Status:** 代码已落地；本机无 SSMS/VSSDK，需在 SSMS 22 环境编译验证。

## Done

1. `ScriptFactoryAccess`：枚举已连接会话、`GetDatabases`、`CloneWithDatabase`、`TryCreateUiConnectionInfo`
2. Quick Search UI：服务器/数据库下拉、刷新、刷新索引
3. Script：使用搜索目标连接（含 TrustServerCertificate）
4. 并行扫库（并发 4）+ 同库 UNION ALL
5. `QuickSearchIndexStore`：`meta.json` + `objects.jsonl`，有索引优先本地搜

## Verify on SSMS 22

1. Object Explorer 连多台服务器 → 下拉能列出
2. 选库 / 全部库搜索正确
3. Script 不再因证书链不受信任失败（OE 连接本身已信任时）
4. 多库搜索比改前快
5. 刷新索引后再次搜索走本地索引，状态栏显示时间

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using EnvDTE;
using Microsoft.SqlServer.Management.UI.VSIntegration;

namespace AxialSqlTools
{
    /// <summary>
    /// Tab History 采集入口。内容相对该文档上次已写入快照未变化时不写盘；
    /// 关闭时文档可能已不可读，平时缓存最新全文，关闭时回退到缓存/磁盘临时文件。
    /// </summary>
    public static class TabHistoryRecorder
    {
        private static readonly ConcurrentDictionary<string, string> LastContentByDocument =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> LastWrittenHashByDocument =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static void RecordWindowEvent(TabHistoryEventType eventType, EnvDTE.Window window)
        {
            try
            {
                if (!UiSettingsStore.GetTabHistoryEnabled()) return;
                if (window == null) return;

                // 激活：只刷新内存中的最新全文缓存，不写盘
                if (eventType == TabHistoryEventType.Activated)
                {
                    if (IsDocumentWindow(window))
                        CacheContentFromWindow(window);
                    return;
                }

                if (eventType == TabHistoryEventType.Closed)
                {
                    RecordClosed(window);
                    return;
                }

                if (!IsDocumentWindow(window)) return;

                string documentName = NormalizeDocumentName(GetDocumentName(window));
                if (string.IsNullOrEmpty(documentName)) return;

                string documentPath = GetDocumentPath(window);
                string content = ScriptFactoryAccess.GetQueryWindowText(window) ?? string.Empty;
                RememberContent(documentName, documentPath, content);

                string dataSource = string.Empty;
                string database = string.Empty;
                TryGetActiveConnectionInfo(out dataSource, out database);

                TryEnqueue(eventType, documentName, documentPath, dataSource, database, content);
            }
            catch (Exception ex)
            {
                AxialSqlToolsPackage._logger?.Warn(ex, "[TabHistory] record window event failed");
            }
        }

        public static void RecordExecuted(string content, string dataSource, string database)
        {
            try
            {
                if (!UiSettingsStore.GetTabHistoryEnabled()) return;

                content = content ?? string.Empty;
                string documentName = NormalizeDocumentName(TryGetActiveDocumentName());
                string documentPath = TryGetActiveDocumentPath();
                RememberContent(documentName, documentPath, content);

                TryEnqueue(TabHistoryEventType.Executed, documentName, documentPath,
                    dataSource ?? string.Empty, database ?? string.Empty, content);
            }
            catch (Exception ex)
            {
                AxialSqlToolsPackage._logger?.Warn(ex, "[TabHistory] record executed failed");
            }
        }

        private static void RecordClosed(EnvDTE.Window window)
        {
            string documentName = NormalizeDocumentName(GetDocumentName(window));
            string documentPath = GetDocumentPath(window);
            if (string.IsNullOrEmpty(documentName) && string.IsNullOrEmpty(documentPath))
            {
                try { documentName = NormalizeDocumentName(window.Caption ?? string.Empty); } catch { }
            }
            if (string.IsNullOrEmpty(documentName) && string.IsNullOrEmpty(documentPath))
                return;

            string content = string.Empty;
            try
            {
                content = ScriptFactoryAccess.GetQueryWindowText(window) ?? string.Empty;
            }
            catch { }

            string key = GetDocumentKey(documentName, documentPath);
            if (string.IsNullOrEmpty(content) &&
                LastContentByDocument.TryGetValue(key, out string cached) &&
                !string.IsNullOrEmpty(cached))
            {
                content = cached;
            }

            // Caption 规范化后 key 可能对不上，再按文件名扫一遍缓存
            if (string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(documentName))
            {
                foreach (var pair in LastContentByDocument)
                {
                    if (pair.Key.EndsWith(documentName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileName(pair.Key), documentName, StringComparison.OrdinalIgnoreCase))
                    {
                        content = pair.Value;
                        if (string.IsNullOrEmpty(documentPath) &&
                            pair.Key.IndexOfAny(new[] { '\\', '/' }) >= 0)
                        {
                            documentPath = pair.Key;
                        }
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(documentPath) && File.Exists(documentPath))
            {
                try { content = File.ReadAllText(documentPath) ?? string.Empty; } catch { }
            }

            // 内容为空或相对上次写入未变化 → 不写盘
            TryEnqueue(TabHistoryEventType.Closed, documentName, documentPath,
                string.Empty, string.Empty, content);

            key = GetDocumentKey(documentName, documentPath);
            LastContentByDocument.TryRemove(key, out _);
            LastWrittenHashByDocument.TryRemove(key, out _);
        }

        /// <summary>
        /// 在关闭/失焦前刷新该窗口的全文缓存（不写盘）。
        /// </summary>
        public static void RememberWindowContent(EnvDTE.Window window)
        {
            if (!UiSettingsStore.GetTabHistoryEnabled()) return;
            if (window == null) return;
            try
            {
                if (IsDocumentWindow(window))
                    CacheContentFromWindow(window);
            }
            catch { }
        }

        private static void CacheContentFromWindow(EnvDTE.Window window)
        {
            try
            {
                string documentName = NormalizeDocumentName(GetDocumentName(window));
                string documentPath = GetDocumentPath(window);
                if (string.IsNullOrEmpty(documentName) && string.IsNullOrEmpty(documentPath))
                    return;
                string content = ScriptFactoryAccess.GetQueryWindowText(window) ?? string.Empty;
                RememberContent(documentName, documentPath, content);
            }
            catch { }
        }

        private static void RememberContent(string documentName, string documentPath, string content)
        {
            if (string.IsNullOrEmpty(content)) return;
            LastContentByDocument[GetDocumentKey(documentName, documentPath)] = content;
        }

        /// <summary>
        /// 空内容或相对该文档上次已写入内容未变化时跳过。
        /// </summary>
        private static void TryEnqueue(
            TabHistoryEventType eventType,
            string documentName,
            string documentPath,
            string dataSource,
            string database,
            string content)
        {
            content = content ?? string.Empty;
            if (string.IsNullOrEmpty(content))
                return;

            string key = GetDocumentKey(documentName, documentPath);
            string hash = ComputeHash(content);
            if (LastWrittenHashByDocument.TryGetValue(key, out string previousHash) &&
                string.Equals(previousHash, hash, StringComparison.Ordinal))
            {
                return;
            }

            LastWrittenHashByDocument[key] = hash;
            TabHistoryStore.Enqueue(new TabHistoryRecord
            {
                Timestamp = DateTime.Now,
                EventType = eventType,
                DocumentName = documentName ?? string.Empty,
                DocumentPath = documentPath ?? string.Empty,
                DataSource = dataSource ?? string.Empty,
                DatabaseName = database ?? string.Empty,
                Content = content,
                ContentHash = hash,
                CharCount = content.Length
            });
        }

        private static string GetDocumentKey(string documentName, string documentPath)
        {
            if (!string.IsNullOrEmpty(documentPath)) return documentPath;
            if (!string.IsNullOrEmpty(documentName)) return documentName;
            return "(unknown)";
        }

        /// <summary>
        /// SSMS 关闭时 Caption 常为 "xxx.sql - server.db (login)"，只保留文件名部分。
        /// </summary>
        internal static string NormalizeDocumentName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            name = name.Trim();
            int sep = name.IndexOf(" - ", StringComparison.Ordinal);
            if (sep > 0)
            {
                string maybeFile = name.Substring(0, sep).Trim();
                if (maybeFile.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                    return maybeFile;
            }
            return name;
        }

        private static bool IsDocumentWindow(EnvDTE.Window window)
        {
            try
            {
                if (!string.Equals(window.Kind, "Document", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return window.Document?.Object("TextDocument") as TextDocument != null;
            }
            catch
            {
                return false;
            }
        }

        private static string GetDocumentName(EnvDTE.Window window)
        {
            try
            {
                if (window.Document != null && !string.IsNullOrEmpty(window.Document.Name))
                {
                    return window.Document.Name;
                }
            }
            catch
            {
            }
            try
            {
                return window.Caption ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetDocumentPath(EnvDTE.Window window)
        {
            try
            {
                return window.Document?.FullName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void TryGetActiveConnectionInfo(out string dataSource, out string database)
        {
            dataSource = string.Empty;
            database = string.Empty;
            try
            {
                var info = ScriptFactoryAccess.GetCurrentConnectionInfo();
                dataSource = info?.ServerName ?? string.Empty;
                database = info?.Database ?? string.Empty;
            }
            catch (Exception ex)
            {
                AxialSqlToolsPackage._logger?.Warn(ex, "[TabHistory] failed to read active connection info");
            }
        }

        private static string TryGetActiveDocumentName()
        {
            try
            {
                var active = ServiceCache.ExtensibilityModel?.ActiveWindow;
                if (active == null) return string.Empty;
                return GetDocumentName(active);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string TryGetActiveDocumentPath()
        {
            try
            {
                var active = ServiceCache.ExtensibilityModel?.ActiveWindow;
                if (active == null) return string.Empty;
                return GetDocumentPath(active);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ComputeHash(string content)
        {
            content = content ?? string.Empty;
            using (var sha1 = SHA1.Create())
            {
                byte[] bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(content));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}

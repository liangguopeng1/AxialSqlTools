using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EnvDTE;
using Microsoft.SqlServer.Management.UI.VSIntegration;

namespace AxialSqlTools
{
    /// <summary>
    /// Tab History 采集入口。在 Package 已挂载的窗口事件与执行完成回调中调用；
    /// 内部做内容哈希去重（同一文档内容未变时 Content 存 null）。
    /// </summary>
    public static class TabHistoryRecorder
    {
        private static readonly ConcurrentDictionary<string, string> LastContentHashes =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static void RecordWindowEvent(TabHistoryEventType eventType, EnvDTE.Window window)
        {
            try
            {
                if (!UiSettingsStore.GetTabHistoryEnabled()) return;
                if (window == null) return;
                if (!IsDocumentWindow(window)) return;

                string documentName = GetDocumentName(window);
                if (string.IsNullOrEmpty(documentName)) return;

                string content = ScriptFactoryAccess.GetQueryWindowText(window);
                string documentKey = GetDocumentKey(documentName, GetDocumentPath(window));
                string hash = ComputeHash(content);

                bool contentChanged = true;
                if (LastContentHashes.TryGetValue(documentKey, out string previousHash) &&
                    string.Equals(previousHash, hash, StringComparison.Ordinal))
                {
                    contentChanged = false;
                }
                LastContentHashes[documentKey] = hash;

                // 关闭事件时连接已不可靠，规格要求不写活动连接
                string dataSource = string.Empty;
                string database = string.Empty;
                if (eventType != TabHistoryEventType.Closed)
                {
                    TryGetActiveConnectionInfo(out dataSource, out database);
                }

                var record = new TabHistoryRecord
                {
                    Timestamp = DateTime.Now,
                    EventType = eventType,
                    DocumentName = documentName,
                    DocumentPath = GetDocumentPath(window),
                    DataSource = dataSource,
                    DatabaseName = database,
                    Content = contentChanged ? content : null,
                    ContentHash = hash,
                    CharCount = contentChanged ? (content?.Length ?? 0) : 0
                };

                TabHistoryStore.Enqueue(record);
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

                // Executed 不参与内容去重：执行内容必须保留
                string documentName = TryGetActiveDocumentName();
                string documentPath = TryGetActiveDocumentPath();
                string hash = ComputeHash(content);
                LastContentHashes[GetDocumentKey(documentName, documentPath)] = hash;

                var record = new TabHistoryRecord
                {
                    Timestamp = DateTime.Now,
                    EventType = TabHistoryEventType.Executed,
                    DocumentName = documentName,
                    DocumentPath = documentPath,
                    DataSource = dataSource,
                    DatabaseName = database,
                    Content = content,
                    ContentHash = hash,
                    CharCount = content?.Length ?? 0
                };

                TabHistoryStore.Enqueue(record);
            }
            catch (Exception ex)
            {
                AxialSqlToolsPackage._logger?.Warn(ex, "[TabHistory] record executed failed");
            }
        }

        private static bool IsDocumentWindow(EnvDTE.Window window)
        {
            try
            {
                // 仅记录文档窗口（SQL 查询编辑器 Kind == "Document"），跳过工具窗/对象资源管理器等
                if (!string.Equals(window.Kind, "Document", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                // 校验底层确为文本文档，避免非查询文档（如设计器）也被记录
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

        private static string GetDocumentKey(string documentName, string documentPath)
        {
            string key = string.IsNullOrEmpty(documentPath) ? documentName : documentPath;
            return string.IsNullOrEmpty(key) ? "(unknown)" : key;
        }

        private static void TryGetActiveConnectionInfo(out string dataSource, out string database)
        {
            dataSource = string.Empty;
            database = string.Empty;
            try
            {
                // 一次调用同时取服务器与数据库，避免重复读取活动连接
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

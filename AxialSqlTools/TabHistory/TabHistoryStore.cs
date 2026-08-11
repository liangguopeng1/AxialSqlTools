using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AxialSqlTools
{
    /// <summary>
    /// Tab History 存储层：后台队列写 JSONL（按天分文件），支持读取与按保留天数清理。
    /// </summary>
    public static class TabHistoryStore
    {
        private static readonly ConcurrentQueue<TabHistoryRecord> WriteQueue = new ConcurrentQueue<TabHistoryRecord>();
        private static readonly object FileSyncRoot = new object();
        private static int _workerRunning;

        public static void Enqueue(TabHistoryRecord record)
        {
            if (record == null) return;
            WriteQueue.Enqueue(record);
            if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
            {
                _ = Task.Run(() => ProcessQueueAsync());
            }
        }

        private static void ProcessQueueAsync()
        {
            try
            {
                while (WriteQueue.TryDequeue(out TabHistoryRecord record))
                {
                    try
                    {
                        AppendLine(record);
                    }
                    catch (Exception ex)
                    {
                        AxialSqlToolsPackage._logger?.Error(ex, "[TabHistory] append failed");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _workerRunning, 0);
                // 写盘期间又有入队则再跑一轮，避免漏写
                if (!WriteQueue.IsEmpty &&
                    Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
                {
                    _ = Task.Run(() => ProcessQueueAsync());
                }
            }
        }

        private static void AppendLine(TabHistoryRecord record)
        {
            string folder = UserConfigPaths.TabHistoryDirectory;
            Directory.CreateDirectory(folder);
            // 按记录时间分天（而非写入时刻），使历史数据按事件日期归档
            string fileName = $"tab-history-{record.Timestamp:yyyy-MM-dd}.jsonl";
            string filePath = Path.Combine(folder, fileName);
            string json = JsonConvert.SerializeObject(record);
            lock (FileSyncRoot)
            {
                File.AppendAllText(filePath, json + Environment.NewLine);
            }
        }

        public static List<TabHistoryRecord> LoadRecent(int max = 1000)
        {
            var records = new List<TabHistoryRecord>();
            string folder = UserConfigPaths.TabHistoryDirectory;
            if (!Directory.Exists(folder)) return records;

            // 文件名按天命名，字典序即时间序；倒序遍历（新的在前）
            string[] files = Directory.GetFiles(folder, "tab-history-*.jsonl", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            Array.Reverse(files);

            foreach (string filePath in files)
            {
                try
                {
                    foreach (string line in File.ReadLines(filePath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var rec = JsonConvert.DeserializeObject<TabHistoryRecord>(line);
                            if (rec == null) continue;
                            records.Add(rec);
                        }
                        catch
                        {
                            // 忽略损坏行，继续
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 单文件读取失败跳过该文件，不影响其他文件
                    AxialSqlToolsPackage._logger?.Warn(ex, "[TabHistory] failed to read {0}", filePath);
                    continue;
                }
            }

            // 同一文档只保留最新一条；Content 为空时从同文档更早快照回填
            return DeduplicateByDocument(records, max);
        }

        private static List<TabHistoryRecord> DeduplicateByDocument(List<TabHistoryRecord> records, int max)
        {
            if (records == null || records.Count == 0)
                return new List<TabHistoryRecord>();

            // 按文档归并：只保留「有正文」的最新一条；全空内容的文档直接丢弃
            var bestByKey = new Dictionary<string, TabHistoryRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in records.OrderBy(r => r.Timestamp))
            {
                if (string.IsNullOrEmpty(rec.Content))
                    continue;
                string key = GetDocumentKey(rec);
                bestByKey[key] = rec;
            }

            var result = new List<TabHistoryRecord>(bestByKey.Count);
            foreach (var best in bestByKey.Values)
            {
                best.DocumentName = TabHistoryRecorder.NormalizeDocumentName(best.DocumentName);
                best.ContentShort = BuildShortText(best.Content);
                if (best.CharCount <= 0)
                    best.CharCount = best.Content.Length;
                result.Add(best);
            }

            return result
                .OrderByDescending(r => r.Timestamp)
                .Take(max)
                .ToList();
        }

        private static string GetDocumentKey(TabHistoryRecord record)
        {
            if (record == null) return "(unknown)";
            if (!string.IsNullOrEmpty(record.DocumentPath))
                return record.DocumentPath;
            string name = TabHistoryRecorder.NormalizeDocumentName(record.DocumentName);
            if (!string.IsNullOrEmpty(name))
                return name;
            return "(unknown)";
        }

        public static void CleanupOldFiles()
        {
            try
            {
                int retentionDays = UiSettingsStore.GetTabHistoryRetentionDays();
                if (retentionDays <= 0) return;

                string folder = UserConfigPaths.TabHistoryDirectory;
                if (!Directory.Exists(folder)) return;

                DateTime cutoff = DateTime.Now.AddDays(-retentionDays);
                foreach (string filePath in Directory.GetFiles(folder, "tab-history-*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (File.GetLastWriteTime(filePath) < cutoff)
                        {
                            File.Delete(filePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        AxialSqlToolsPackage._logger?.Debug(ex, "[TabHistory] cleanup failed to delete {0}", filePath);
                    }
                }
            }
            catch (Exception ex)
            {
                AxialSqlToolsPackage._logger?.Error(ex, "[TabHistory] cleanup failed");
            }
        }

        private static string BuildShortText(string content)
        {
            content = content ?? string.Empty;
            return content.Length > 100 ? content.Substring(0, 100) : content;
        }
    }
}

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

            // 文件名按天命名，字典序即时间序；倒序遍历（新的在前），
            // 每读一个文件后累积数量达到 max 即停止，避免全量物化
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
                            rec.ContentShort = BuildShortText(rec.Content);
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
                if (records.Count >= max) break;
            }

            // 最终统一按 Timestamp 倒序（跨文件），取前 max 条
            return records
                .OrderByDescending(r => r.Timestamp)
                .Take(max)
                .ToList();
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

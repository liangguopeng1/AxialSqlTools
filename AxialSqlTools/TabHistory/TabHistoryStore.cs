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
            string fileName = $"tab-history-{DateTime.Now:yyyy-MM-dd}.jsonl";
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

            foreach (string filePath in Directory.GetFiles(folder, "tab-history-*.jsonl", SearchOption.TopDirectoryOnly))
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
                    catch
                    {
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

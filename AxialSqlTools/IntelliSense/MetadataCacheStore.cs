using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AxialSqlTools.IntelliSense
{
    internal sealed class IntelliSenseCacheMeta
    {
        public string ServerName { get; set; }
        public DateTime IndexedAtUtc { get; set; }
        public List<string> Databases { get; set; } = new List<string>();
        public string ConnectionFingerprint { get; set; }
        /// <summary>本机 sys.servers 中 is_linked=1 的名称，供 FROM 补全。远程库目录另按链接服务器名分目录缓存。</summary>
        public List<string> LinkedServers { get; set; } = new List<string>();
    }

    /// <summary>
    /// IntelliSense 目录磁盘缓存：%APPDATA%\AxialSqlTools\intellisense-cache\{server}\
    /// meta.json + 每库一份 {database}.json。不与快速搜索 objects.jsonl 共用。
    /// </summary>
    internal static class MetadataCacheStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        public static string GetServerDirectory(string serverName)
        {
            return Path.Combine(UserConfigPaths.IntelliSenseCacheDirectory, UserConfigPaths.SanitizePathSegment(serverName));
        }

        public static string GetMetaPath(string serverName)
        {
            return Path.Combine(GetServerDirectory(serverName), "meta.json");
        }

        public static string GetCatalogPath(string serverName, string database)
        {
            return Path.Combine(GetServerDirectory(serverName), UserConfigPaths.SanitizePathSegment(database) + ".json");
        }

        public static bool TryGetMeta(string serverName, out IntelliSenseCacheMeta meta)
        {
            meta = null;
            if (string.IsNullOrWhiteSpace(serverName))
                return false;
            string path = GetMetaPath(serverName);
            if (!File.Exists(path))
                return false;
            try
            {
                meta = JsonConvert.DeserializeObject<IntelliSenseCacheMeta>(File.ReadAllText(path));
                return meta != null;
            }
            catch
            {
                return false;
            }
        }

        public static MetadataCatalog TryLoadCatalog(string serverName, string database)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(database))
                return null;
            string path = GetCatalogPath(serverName, database);
            if (!File.Exists(path))
                return null;
            try
            {
                var catalog = JsonConvert.DeserializeObject<MetadataCatalog>(File.ReadAllText(path));
                if (catalog == null)
                    return null;
                if (catalog.IsEmpty && !catalog.IsIndexed)
                    return null;
                if (string.IsNullOrEmpty(catalog.Database))
                    catalog.Database = database;
                if (string.IsNullOrEmpty(catalog.Server))
                    catalog.Server = serverName;
                return catalog;
            }
            catch
            {
                return null;
            }
        }

        public static IEnumerable<string> ListCatalogFiles(string serverName)
        {
            string dir = GetServerDirectory(serverName);
            if (!Directory.Exists(dir))
                yield break;
            foreach (string path in Directory.GetFiles(dir, "*.json"))
            {
                string name = Path.GetFileName(path);
                if (string.Equals(name, "meta.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return path;
            }
        }

        /// <summary>
        /// 各库 {database}.json 里最早的 BuiltAt（UTC）。没有可读的库缓存时返回 false。
        /// meta.json 缺失时，7 天静默刷新回退看这个。
        /// </summary>
        public static bool TryGetOldestCatalogBuiltAtUtc(string serverName, out DateTime oldestUtc)
        {
            oldestUtc = DateTime.MinValue;
            DateTime? oldest = null;
            foreach (string path in ListCatalogFiles(serverName))
            {
                DateTime built;
                if (!TryPeekCatalogBuiltAtUtc(path, out built))
                    continue;
                if (oldest == null || built < oldest.Value)
                    oldest = built;
            }
            if (oldest == null)
                return false;
            oldestUtc = oldest.Value;
            return true;
        }

        /// <summary>只读文件头里的 BuiltAt，不反序列化整份目录（库 json 可能很大）。</summary>
        public static bool TryPeekCatalogBuiltAtUtc(string path, out DateTime builtAtUtc)
        {
            builtAtUtc = DateTime.MinValue;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;
            try
            {
                string head;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] buf = new byte[1024];
                    int n = fs.Read(buf, 0, buf.Length);
                    if (n <= 0)
                        return false;
                    head = Encoding.UTF8.GetString(buf, 0, n);
                }
                int idx = head.IndexOf("\"BuiltAt\"", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    return false;
                int colon = head.IndexOf(':', idx);
                if (colon < 0)
                    return false;
                int q1 = head.IndexOf('"', colon);
                if (q1 < 0)
                    return false;
                int q2 = head.IndexOf('"', q1 + 1);
                if (q2 < 0)
                    return false;
                string raw = head.Substring(q1 + 1, q2 - q1 - 1);
                DateTimeOffset dto;
                if (!DateTimeOffset.TryParse(raw, null, DateTimeStyles.RoundtripKind, out dto))
                    return false;
                builtAtUtc = dto.UtcDateTime;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void SaveCatalog(string serverName, string database, MetadataCatalog catalog)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(database) || catalog == null)
                return;
            string dir = GetServerDirectory(serverName);
            Directory.CreateDirectory(dir);
            string path = GetCatalogPath(serverName, database);
            WriteAtomic(path, JsonConvert.SerializeObject(catalog, JsonSettings));
        }

        public static void SaveMeta(string serverName, IntelliSenseCacheMeta meta)
        {
            if (string.IsNullOrWhiteSpace(serverName) || meta == null)
                return;
            string dir = GetServerDirectory(serverName);
            Directory.CreateDirectory(dir);
            WriteAtomic(GetMetaPath(serverName), JsonConvert.SerializeObject(meta, Formatting.Indented, JsonSettings));
        }

        /// <summary>删除单库目录缓存文件（DDL 失效时调用，强制下次重建）。</summary>
        public static void DeleteCatalog(string serverName, string database)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(database))
                return;
            try
            {
                string path = GetCatalogPath(serverName, database);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 删除失败不阻塞（下次刷新仍会覆盖）
            }
        }

        /// <summary>删除服务器 meta.json（库列表/链接服务器名；过期判断会回退看各库 BuiltAt）。</summary>
        public static void DeleteMeta(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName))
                return;
            try
            {
                string path = GetMetaPath(serverName);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 忽略删除失败
            }
        }

        private static void WriteAtomic(string path, string json)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json ?? string.Empty, new UTF8Encoding(false));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmp, path);
        }
    }
}

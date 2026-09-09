using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AxialSqlTools.IntelliSense
{
    internal sealed class IntelliSenseCacheMeta
    {
        public string ServerName { get; set; }
        /// <summary>整机索引完成时间，磁盘按 GMT+8（+08:00）写入。</summary>
        public DateTimeOffset IndexedAtUtc { get; set; }
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
                using (var stream = File.OpenRead(path))
                using (var reader = new StreamReader(stream))
                using (var json = new JsonTextReader(reader))
                {
                    var catalog = JsonSerializer.Create(JsonSettings).Deserialize<MetadataCatalog>(json);
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

        /// <summary>删除服务器 meta.json（库列表/链接服务器名；7 天过期看 IndexedAtUtc）。</summary>
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

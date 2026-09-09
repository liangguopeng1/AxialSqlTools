using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// 按服务器刷新全部用户库的 IntelliSense 磁盘缓存。打开标签时加载到内存；缺失/过期则拉取。均显示进度。
    /// </summary>
    public sealed class MetadataCacheRefreshService
    {
        public const int BuildCommandTimeoutSeconds = 60;
        private const int MaxParallelDatabases = 4;

        private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
        private static readonly Lazy<MetadataCacheRefreshService> _instance =
            new Lazy<MetadataCacheRefreshService>(() => new MetadataCacheRefreshService());

        public static MetadataCacheRefreshService Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, ServerJob> _jobs =
            new ConcurrentDictionary<string, ServerJob>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _kicked =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _dbRefreshKicked =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private MetadataCacheRefreshService()
        {
        }

        public bool IsRefreshing(string serverName)
        {
            return !string.IsNullOrWhiteSpace(serverName) && _jobs.ContainsKey(serverName);
        }

        public void Cancel(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName))
                return;
            if (_jobs.TryGetValue(serverName, out var job))
                job.Cts.Cancel();
        }

        /// <summary>打开标签/连接就绪：内存没有则从磁盘加载；缺失或过期则拉取。均显示进度。</summary>
        public void EnsureServerCache(ScriptFactoryAccess.ConnectionInfo connInfo)
        {
            if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                return;
            if (!UiSettingsStore.GetIntelliSenseEnabled())
                return;
            if (!_kicked.TryAdd(connInfo.ServerName, 0))
                return;
            var captured = connInfo;
            string server = captured.ServerName;
            _ = Task.Run(async () =>
            {
                try
                {
                    bool loaded = MetadataCatalogService.Instance.IsServerLoadedInMemory(server);
                    bool needPull = ShouldSilentRefresh(server);
                    Logger.Info("EnsureServerCache {0} loaded={1} needPull={2} source={3}",
                        server, loaded, needPull, needPull ? "server" : "disk");
                    if (!loaded || needPull || MetadataCatalogService.Instance.HasIncompleteSchemaCache(server))
                    {
                        await RunWithProgressWindowAsync(server, async progress =>
                        {
                            if (!MetadataCatalogService.Instance.IsServerLoadedInMemory(server))
                                MetadataCatalogService.Instance.LoadServerFromDisk(server, progress, captured.Database);
                            if (!needPull
                                && !MetadataCatalogService.Instance.HasIncompleteSchemaCache(server))
                                return;
                            Logger.Info("IntelliSense cache load from server {0}", server);
                            await StartOrAttach(captured, progress).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "EnsureServerCache failed for {0}", server);
                    byte removed;
                    _kicked.TryRemove(server, out removed);
                }
            });
        }

        /// <summary>
        /// 经当前连接的四段名，把链接服务器目录拉到磁盘并写入内存。显示进度。
        /// </summary>
        public void EnsureLinkedServerCache(ScriptFactoryAccess.ConnectionInfo connInfo, string linkedServer)
        {
            if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.FullConnectionString))
                return;
            if (string.IsNullOrWhiteSpace(linkedServer))
                return;
            if (!UiSettingsStore.GetIntelliSenseEnabled())
                return;
            if (!_kicked.TryAdd(linkedServer, 0))
                return;
            var captured = connInfo;
            string name = linkedServer;
            _ = Task.Run(async () =>
            {
                try
                {
                    bool loaded = MetadataCatalogService.Instance.IsServerLoadedInMemory(name);
                    bool needPull = ShouldSilentRefresh(name);
                    Logger.Info("EnsureLinkedServerCache {0} loaded={1} needPull={2} source={3}",
                        name, loaded, needPull, needPull ? "server" : "disk");
                    if (!loaded || needPull || MetadataCatalogService.Instance.HasIncompleteSchemaCache(name))
                    {
                        await RunWithProgressWindowAsync(name, async progress =>
                        {
                            if (!MetadataCatalogService.Instance.IsServerLoadedInMemory(name))
                                MetadataCatalogService.Instance.LoadServerFromDisk(name, progress);
                            if (!needPull
                                && !MetadataCatalogService.Instance.HasIncompleteSchemaCache(name))
                                return;
                            Logger.Info("IntelliSense cache load from server {0}", name);
                            await StartLinkedOrAttach(captured, name, progress).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "EnsureLinkedServerCache failed for {0}", name);
                    byte removed;
                    _kicked.TryRemove(name, out removed);
                }
            });
        }

        /// <summary>Package 启动：对已连接 OE 会话尝试预热。</summary>
        public void KickoffFromPackage()
        {
            if (!UiSettingsStore.GetIntelliSenseEnabled())
                return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var sessions = ScriptFactoryAccess.GetConnectedObjectExplorerSessions();
                    if (sessions == null || sessions.Count == 0)
                    {
                        var current = TryCurrentConnection();
                        if (current != null)
                            sessions = new List<ScriptFactoryAccess.ConnectionInfo> { current };
                    }
                    if (sessions == null)
                        return;
                    foreach (var session in sessions)
                        EnsureServerCache(session);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "KickoffFromPackage failed");
                }
            });
        }

        public Task RefreshServerAsync(ScriptFactoryAccess.ConnectionInfo connInfo, IProgress<IndexBuildProgress> progress)
        {
            if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                return Task.CompletedTask;
            return StartOrAttach(connInfo, progress);
        }

        public async Task RefreshWithProgressWindowAsync(ScriptFactoryAccess.ConnectionInfo connInfo)
        {
            if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                throw new ArgumentException("未连接到服务器。");
            Logger.Info("IntelliSense cache load from server {0} (manual refresh)", connInfo.ServerName);
            await RunWithProgressWindowAsync(connInfo.ServerName, progress => RefreshServerAsync(connInfo, progress)).ConfigureAwait(false);
        }

        private async Task RunWithProgressWindowAsync(string serverName, Func<IProgress<IndexBuildProgress>, Task> work)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                await work(null).ConfigureAwait(false);
                return;
            }
            IndexBuildProgressWindow window = null;
            await dispatcher.InvokeAsync(() =>
            {
                window = new IndexBuildProgressWindow();
                window.CancelRequested += () => Cancel(serverName);
                window.ShowAtBottomRight();
            });
            var progress = new Progress<IndexBuildProgress>(p =>
            {
                var w = window;
                if (w == null) return;
                if (w.Dispatcher.CheckAccess())
                    w.Update(p);
                else
                    w.Dispatcher.BeginInvoke(new Action(() => w.Update(p)));
            });
            try
            {
                await work(progress).ConfigureAwait(false);
            }
            finally
            {
                await dispatcher.InvokeAsync(() => window?.CloseSafe());
            }
        }

        /// <summary>7 天整机刷新只看该服务器 intellisense-cache/meta.json 的 IndexedAtUtc。</summary>
        private static bool ShouldSilentRefresh(string serverName)
        {
            int days = UiSettingsStore.GetIntelliSenseSettings().cacheRefreshDays;
            if (days <= 0)
                return false;
            IntelliSenseCacheMeta meta;
            if (!MetadataCacheStore.TryGetMeta(serverName, out meta) || meta == null)
                return true;
            DateTimeOffset indexed = meta.IndexedAtUtc;
            if (indexed == DateTimeOffset.MinValue)
                return true;
            return (DateTimeOffset.UtcNow - indexed) > TimeSpan.FromDays(days);
        }

        /// <summary>
        /// 只重建指定库（执行 DDL 后）。不改 meta.IndexedAtUtc，不影响 7 天整机刷新。
        /// </summary>
        public void RefreshDatabases(ScriptFactoryAccess.ConnectionInfo connInfo, IList<string> databases)
        {
            if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName) || databases == null)
                return;
            if (!UiSettingsStore.GetIntelliSenseEnabled())
                return;
            var pending = new List<string>();
            foreach (var db in databases)
            {
                if (string.IsNullOrWhiteSpace(db)) continue;
                string key = connInfo.ServerName + "|" + db;
                if (_dbRefreshKicked.TryAdd(key, 0))
                    pending.Add(db);
            }
            if (pending.Count == 0)
                return;
            var captured = connInfo;
            _ = Task.Run(() =>
            {
                try
                {
                    foreach (var db in pending)
                    {
                        try
                        {
                            Logger.Info("Refresh database cache {0}.{1}", captured.ServerName, db);
                            var catalog = MetadataCatalogService.Instance.BuildCatalogForCache(
                                captured, db, BuildCommandTimeoutSeconds);
                            if (catalog != null)
                            {
                                MetadataCacheStore.SaveCatalog(captured.ServerName, db, catalog);
                                MetadataCatalogService.Instance.PutCatalog(captured.ServerName, db, catalog);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Refresh database cache failed {0}.{1}", captured.ServerName, db);
                        }
                        byte removed;
                        _dbRefreshKicked.TryRemove(captured.ServerName + "|" + db, out removed);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "RefreshDatabases failed for {0}", captured.ServerName);
                    foreach (var db in pending)
                    {
                        byte removed;
                        _dbRefreshKicked.TryRemove(captured.ServerName + "|" + db, out removed);
                    }
                }
            });
        }

        private static ScriptFactoryAccess.ConnectionInfo TryCurrentConnection()
        {
            try
            {
                return ScriptFactoryAccess.GetCurrentConnectionInfo();
            }
            catch
            {
                return null;
            }
        }

        private readonly object _startLock = new object();

        private Task StartOrAttach(ScriptFactoryAccess.ConnectionInfo connInfo, IProgress<IndexBuildProgress> progress)
        {
            string key = connInfo.ServerName;
            ServerJob job;
            lock (_startLock)
            {
                if (!_jobs.TryGetValue(key, out job))
                {
                    job = new ServerJob();
                    _jobs[key] = job;
                    job.Task = Task.Run(() => RunRefresh(connInfo, job));
                }
            }
            job.Attach(progress);
            return job.Task;
        }

        private Task StartLinkedOrAttach(
            ScriptFactoryAccess.ConnectionInfo viaConn,
            string linkedServer,
            IProgress<IndexBuildProgress> progress)
        {
            ServerJob job;
            lock (_startLock)
            {
                if (!_jobs.TryGetValue(linkedServer, out job))
                {
                    job = new ServerJob();
                    _jobs[linkedServer] = job;
                    job.Task = Task.Run(() => RunLinkedRefresh(viaConn, linkedServer, job));
                }
            }
            job.Attach(progress);
            return job.Task;
        }

        private void RunRefresh(ScriptFactoryAccess.ConnectionInfo connInfo, ServerJob job)
        {
            string server = connInfo.ServerName;
            try
            {
                List<string> databases;
                try
                {
                    databases = ScriptFactoryAccess.GetDatabases(connInfo) ?? new List<string>();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "GetDatabases failed during cache refresh {0}", server);
                    job.Report(new IndexBuildProgress
                    {
                        ServerName = server,
                        Message = "无法枚举数据库",
                        Completed = 0,
                        Total = 0
                    });
                    return;
                }

                MetadataCatalogService.Instance.PutDatabaseList(server, databases);
                var linkedServers = MetadataCatalogService.Instance.QueryLinkedServersForCache(connInfo);
                MetadataCatalogService.Instance.PutLinkedServers(server, linkedServers);
                int total = databases.Count + 1;
                int completed = 0;
                Logger.Info("IntelliSense cache load from server {0} databases={1} + sys", server, databases.Count);
                job.Report(new IndexBuildProgress
                {
                    ServerName = server,
                    Title = "从服务器加载 IntelliSense 缓存",
                    Completed = 0,
                    Total = total,
                    CurrentItem = "sys",
                    Message = "服务器  0/" + total + "  sys（整机共用）"
                });
                try
                {
                    var sysCatalog = MetadataCatalogService.Instance.BuildSystemCatalogForCache(
                        connInfo, BuildCommandTimeoutSeconds);
                    if (sysCatalog != null)
                    {
                        MetadataCacheStore.SaveSystemCatalog(server, sysCatalog);
                        MetadataCatalogService.Instance.PutSystemCatalog(server, sysCatalog);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Build system catalog failed {0}", server);
                }
                completed = 1;
                job.Report(new IndexBuildProgress
                {
                    ServerName = server,
                    CurrentItem = "sys",
                    Completed = completed,
                    Total = total,
                    Message = "服务器  1/" + total + "  sys（整机共用）"
                });

                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxParallelDatabases,
                    CancellationToken = job.Cts.Token
                };
                Parallel.ForEach(databases, options, database =>
                {
                    job.Cts.Token.ThrowIfCancellationRequested();
                    int started = Volatile.Read(ref completed);
                    job.Report(new IndexBuildProgress
                    {
                        ServerName = server,
                        CurrentItem = database,
                        Completed = started,
                        Total = total,
                        Message = "服务器  " + started + "/" + total + "  " + database
                    });
                    try
                    {
                        var catalog = MetadataCatalogService.Instance.BuildCatalogForCache(
                            connInfo, database, BuildCommandTimeoutSeconds);
                        if (catalog != null)
                        {
                            MetadataCacheStore.SaveCatalog(server, database, catalog);
                            MetadataCatalogService.Instance.PutCatalog(server, database, catalog);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Build catalog failed {0}.{1}", server, database);
                    }
                    int done = Interlocked.Increment(ref completed);
                    job.Report(new IndexBuildProgress
                    {
                        ServerName = server,
                        CurrentItem = database,
                        Completed = done,
                        Total = total,
                        Message = "服务器  " + done + "/" + total + "  " + database
                    });
                });

                MetadataCacheStore.SaveMeta(server, new IntelliSenseCacheMeta
                {
                    ServerName = server,
                    IndexedAtUtc = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)),
                    Databases = databases,
                    LinkedServers = linkedServers,
                    ConnectionFingerprint = ScriptFactoryAccess.BuildConnectionFingerprint(connInfo)
                });
                MetadataCatalogService.Instance.MarkServerLoadedInMemory(server);
                job.Report(new IndexBuildProgress
                {
                    ServerName = server,
                    Completed = total,
                    Total = total,
                    Message = "服务器  完成（" + total + " 个库）"
                });
            }
            catch (OperationCanceledException)
            {
                job.Report(new IndexBuildProgress
                {
                    ServerName = server,
                    Message = "已取消"
                });
            }
            catch (AggregateException ae)
            {
                if (job.Cts.IsCancellationRequested)
                {
                    job.Report(new IndexBuildProgress
                    {
                        ServerName = server,
                        Message = "已取消"
                    });
                }
                else
                {
                    Logger.Warn(ae, "Cache refresh failed for {0}", server);
                    job.Report(new IndexBuildProgress
                    {
                        ServerName = server,
                        Message = "刷新失败"
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Cache refresh failed for {0}", server);
                job.Report(new IndexBuildProgress
                {
                    ServerName = server,
                    Message = "刷新失败"
                });
            }
            finally
            {
                ServerJob removed;
                _jobs.TryRemove(server, out removed);
            }
        }

        private void RunLinkedRefresh(
            ScriptFactoryAccess.ConnectionInfo viaConn,
            string linkedServer,
            ServerJob job)
        {
            try
            {
                Logger.Info("Linked cache pull start {0} via {1}", linkedServer, viaConn?.ServerName);
                List<string> databases = MetadataCatalogService.Instance.QueryLinkedServerDatabasesForCache(
                    viaConn, linkedServer, BuildCommandTimeoutSeconds) ?? new List<string>();
                if (databases.Count == 0)
                    Logger.Warn("Linked cache pull got 0 databases for {0} via {1}", linkedServer, viaConn?.ServerName);
                MetadataCatalogService.Instance.PutDatabaseList(linkedServer, databases);
                int total = databases.Count + 1;
                int completed = 0;
                job.Report(new IndexBuildProgress
                {
                    ServerName = linkedServer,
                    Title = "从服务器加载链接服务器缓存",
                    Completed = 0,
                    Total = total,
                    CurrentItem = "sys",
                    Message = "服务器  0/" + total + "  sys（整机共用）"
                });
                try
                {
                    var sysCatalog = MetadataCatalogService.Instance.BuildLinkedSystemCatalogForCache(
                        viaConn, linkedServer, BuildCommandTimeoutSeconds);
                    if (sysCatalog != null)
                    {
                        MetadataCacheStore.SaveSystemCatalog(linkedServer, sysCatalog);
                        MetadataCatalogService.Instance.PutSystemCatalog(linkedServer, sysCatalog);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Build linked system catalog failed {0}", linkedServer);
                }
                completed = 1;
                job.Report(new IndexBuildProgress
                {
                    ServerName = linkedServer,
                    CurrentItem = "sys",
                    Completed = completed,
                    Total = total,
                    Message = "服务器  1/" + total + "  sys（整机共用）"
                });
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxParallelDatabases,
                    CancellationToken = job.Cts.Token
                };
                Parallel.ForEach(databases, options, database =>
                {
                    job.Cts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var catalog = MetadataCatalogService.Instance.BuildLinkedCatalogForCache(
                            viaConn, linkedServer, database, BuildCommandTimeoutSeconds);
                        if (catalog != null)
                        {
                            MetadataCacheStore.SaveCatalog(linkedServer, database, catalog);
                            MetadataCatalogService.Instance.PutCatalog(linkedServer, database, catalog);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Linked catalog failed {0}.{1}", linkedServer, database);
                    }
                    int done = Interlocked.Increment(ref completed);
                    job.Report(new IndexBuildProgress
                    {
                        ServerName = linkedServer,
                        CurrentItem = database,
                        Completed = done,
                        Total = total,
                        Message = "服务器  " + done + "/" + total + "  " + database
                    });
                });
                MetadataCacheStore.SaveMeta(linkedServer, new IntelliSenseCacheMeta
                {
                    ServerName = linkedServer,
                    IndexedAtUtc = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)),
                    Databases = databases,
                    ConnectionFingerprint = "linked:" + (viaConn?.ServerName ?? string.Empty)
                });
                MetadataCatalogService.Instance.MarkServerLoadedInMemory(linkedServer);
                Logger.Info("Linked cache pull done {0} dbs={1}", linkedServer, total);
                job.Report(new IndexBuildProgress
                {
                    ServerName = linkedServer,
                    Completed = total,
                    Total = total,
                    Message = "完成链接服务器（" + total + " 个库）"
                });
            }
            catch (OperationCanceledException)
            {
                job.Report(new IndexBuildProgress
                {
                    ServerName = linkedServer,
                    Message = "已取消"
                });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Linked cache pull failed for {0}", linkedServer);
                job.Report(new IndexBuildProgress
                {
                    ServerName = linkedServer,
                    Message = "链接服务器刷新失败"
                });
            }
            finally
            {
                ServerJob removed;
                _jobs.TryRemove(linkedServer, out removed);
            }
        }

        private sealed class ServerJob
        {
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
            public Task Task;
            public IndexBuildProgress LastProgress;
            private readonly List<IProgress<IndexBuildProgress>> _listeners = new List<IProgress<IndexBuildProgress>>();
            private readonly object _lock = new object();

            public void Attach(IProgress<IndexBuildProgress> progress)
            {
                if (progress == null) return;
                IndexBuildProgress last;
                lock (_lock)
                {
                    _listeners.Add(progress);
                    last = LastProgress;
                }
                if (last != null)
                    progress.Report(last);
            }

            public void Report(IndexBuildProgress progress)
            {
                List<IProgress<IndexBuildProgress>> snapshot;
                lock (_lock)
                {
                    LastProgress = progress;
                    snapshot = new List<IProgress<IndexBuildProgress>>(_listeners);
                }
                foreach (var listener in snapshot)
                {
                    try { listener.Report(progress); }
                    catch { }
                }
            }
        }
    }
}

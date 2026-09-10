using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AxialSqlTools
{
    namespace IntelliSense
    {
        public partial class CompletionEngine
        {
            #region 候选生成

            private List<CompletionItem> BuildItems(
                CompletionContext ctx,
                string prefix,
                FromObjectNameContext fromName,
                LocalSymbols local,
                MetadataCatalog catalog,
                IntelliSenseSettings settings,
                ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                var items = new List<CompletionItem>();

                switch (ctx)
                {
                    case CompletionContext.BatchStart:
                        AddSnippets(items, prefix);
                        if (settings.includeKeywords)
                        {
                            AddKeywords(items, TopLevelKeywords);
                            AddKeywords(items, AfterFromKeywords);
                        }
                        break;

                    case CompletionContext.AfterCreate:
                        if (settings.includeKeywords) AddKeywords(items, CreateObjectKeywords);
                        break;

                    case CompletionContext.AfterAlter:
                        if (settings.includeKeywords) AddKeywords(items, AlterObjectKeywords);
                        break;

                    case CompletionContext.AfterTruncate:
                        if (settings.includeKeywords)
                            AddKeywords(items, new[] { "TABLE" });
                        break;

                    case CompletionContext.AfterDrop:
                        if (settings.includeKeywords) AddKeywords(items, DropObjectKeywords);
                        break;

                    case CompletionContext.DdlObjectTarget:
                        AddFromClauseItems(items, fromName, catalog, settings, connInfo, prefix, local);
                        break;

                    case CompletionContext.FromClause:
                        if (fromName != null && (fromName.ExcessiveDots || IsDoubleDotAfterSchema(fromName, connInfo)))
                            break;
                        AddFromClauseItems(items, fromName, catalog, settings, connInfo, prefix, local);
                        if (settings.includeKeywords && ShouldSuggestAfterFromKeywords(fromName, prefix))
                        {
                            AddKeywords(items, AfterFromKeywords);
                            // JOIN ON 条件续写
                            if (!string.IsNullOrEmpty(prefix))
                                AddKeywords(items, new[] { "AND", "OR" });
                        }
                        break;

                    case CompletionContext.InsertTarget:
                        if (settings.includeKeywords) AddKeywords(items, InsertKeywords);
                        AddFromClauseItems(items, fromName, catalog, settings, connInfo, prefix, local);
                        break;

                    case CompletionContext.InsertColumnList:
                        AddColumnsFromFromAliases(items, catalog, local, settings, connInfo);
                        AddAllColumnsCompletions(items, catalog, local, settings, connInfo, withAliasPrefix: false);
                        break;

                    case CompletionContext.UpdateTarget:
                    case CompletionContext.DeleteTarget:
                        AddFromClauseItems(items, fromName, catalog, settings, connInfo, prefix, local);
                        if (settings.includeKeywords && !string.IsNullOrEmpty(prefix)
                            && (fromName == null || !fromName.AfterDot))
                            AddKeywords(items, new[] { "SET", "FROM", "WHERE" });
                        break;

                    case CompletionContext.SelectElements:
                        if (fromName != null && fromName.InSelectList
                            && (fromName.AfterDot || fromName.UsesDoubleDot))
                        {
                            AddSelectFunctionItems(items, fromName, catalog, settings, connInfo, prefix);
                            break;
                        }
                        if (settings.includeKeywords)
                        {
                            AddKeywords(items, SelectClauseKeywords);
                        }
                        AddColumnsAndFunctions(items, catalog, local, settings, connInfo);
                        AddScalarFunctionsFromCatalog(items, catalog, settings, fromName, connInfo, prefix);
                        AddDatabases(items, connInfo, settings);
                        break;

                    case CompletionContext.SelectAlias:
                        if (settings.includeKeywords)
                            AddKeywords(items, new[] { "AS" });
                        break;

                    case CompletionContext.TableHint:
                        if (settings.includeKeywords)
                            AddKeywords(items, TableHintKeywords);
                        break;

                    case CompletionContext.WhereClause:
                        AddAliasCompletions(items, local, settings, prefix);
                        AddColumnsFromFromAliases(items, catalog, local, settings, connInfo);
                        AddBuiltInFunctions(items);
                        if (settings.includeKeywords && !string.IsNullOrEmpty(prefix))
                        {
                            // 别名仅抑制 AND/OR 等运算符，不抑制 GROUP BY/WHERE 子句关键字
                            if (!PrefixLooksLikeAlias(prefix, local))
                                AddKeywords(items, WhereOperatorKeywords);
                            AddKeywords(items, CaseKeywords);
                            AddKeywords(items, AfterWhereKeywords);
                            if (IsExecStatementPrefix(prefix))
                                AddKeywords(items, TopLevelKeywords);
                        }
                        break;

                    case CompletionContext.HavingClause:
                        AddAliasCompletions(items, local, settings, prefix);
                        AddGroupByColumns(items, local, settings);
                        AddBuiltInFunctions(items);
                        if (settings.includeKeywords && !string.IsNullOrEmpty(prefix))
                        {
                            if (!PrefixLooksLikeAlias(prefix, local))
                                AddKeywords(items, WhereOperatorKeywords);
                            AddKeywords(items, AfterWhereKeywords);
                        }
                        break;

                    case CompletionContext.UpdateSet:
                        AddAliasCompletions(items, local, settings, prefix);
                        AddColumnsFromFromAliases(items, catalog, local, settings, connInfo);
                        AddBuiltInFunctions(items);
                        if (settings.includeKeywords && !string.IsNullOrEmpty(prefix)
                            && !PrefixLooksLikeAlias(prefix, local))
                            AddKeywords(items, OperatorKeywords);
                        break;

                    case CompletionContext.OrderByGroupBy:
                        AddAliasCompletions(items, local, settings, prefix);
                        AddColumnsFromFromAliases(items, catalog, local, settings, connInfo);
                        AddBuiltInFunctions(items);
                        if (settings.includeKeywords)
                        {
                            AddKeywords(items, GroupOrderByKeywords);
                            if (!string.IsNullOrEmpty(prefix) && !PrefixLooksLikeAlias(prefix, local))
                            {
                                AddKeywords(items, OperatorKeywords);
                                AddKeywords(items, AfterGroupOrderKeywords);
                            }
                        }
                        break;

                    case CompletionContext.AfterExec:
                        AddExecItems(items, fromName, prefix, catalog, settings, connInfo);
                        break;

                    case CompletionContext.MemberAccess:
                        AddMemberColumns(items, prefix, catalog, local, settings, connInfo);
                        break;

                    case CompletionContext.LocalMemberAccess:
                        AddLocalMemberColumnsFromPrefix(items, prefix, local, settings);
                        break;

                    case CompletionContext.LocalVariable:
                        AddLocalVariables(items, local, settings);
                        break;

                    case CompletionContext.AfterUse:
                        AddDatabases(items, connInfo, settings);
                        break;

                    case CompletionContext.DataType:
                        if (settings.includeKeywords)
                            AddKeywords(items, DataTypeKeywords);
                        break;
                }

                return items;
            }

            private void AddFromClauseItems(
                List<CompletionItem> items,
                FromObjectNameContext fromName,
                MetadataCatalog catalog,
                IntelliSenseSettings settings,
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string namePrefix = null,
                LocalSymbols local = null)
            {
                if (fromName == null || !fromName.InFromClause)
                {
                    AddLocalFromObjects(items, local, settings, namePrefix);
                    AddDatabases(items, connInfo, settings);
                    AddLinkedServers(items, connInfo, settings);
                    AddSchemasFromCatalog(items, catalog, settings, namePrefix);
                    AddTablesViewsAndRoutines(items, catalog, settings, includeTableFunctions: true, fromName: fromName, connInfo: connInfo, namePrefix: namePrefix);
                    return;
                }
                if (fromName.ExcessiveDots || IsDoubleDotAfterSchema(fromName, connInfo))
                    return;

                int segCount = fromName.Segments?.Count ?? 0;
                if (segCount == 0 && !fromName.AfterDot)
                    AddLocalFromObjects(items, local, settings, namePrefix);
                string ipName = JoinNumericFromSegments(fromName);
                if (ipName != null)
                {
                    if (IsCompleteIpv4(ipName))
                        MetadataCacheRefreshService.Instance.EnsureLinkedServerCache(connInfo, ipName);
                    if (IsCompleteIpv4(ipName) && fromName.AfterDot && string.IsNullOrEmpty(fromName.Partial))
                    {
                        AddLinkedServerDatabases(items, connInfo, ipName, fromName, settings);
                        return;
                    }
                    AddLinkedServers(items, connInfo, settings, ipName);
                    return;
                }
                if (segCount > 0 && IsLinkedServerName(connInfo, fromName.Segments[0]))
                {
                    string linkedServer = fromName.Segments[0];
                    if (fromName.AfterDot)
                    {
                        if (segCount == 1)
                        {
                            AddLinkedServerDatabases(items, connInfo, linkedServer, fromName, settings);
                            return;
                        }
                        if (segCount == 2)
                        {
                            var remote = MetadataCatalogService.Instance.GetOrBuildLinkedCatalog(connInfo, linkedServer, fromName.Segments[1]);
                            // server.db.. → 省略 dbo，只出表名；server.db. → 架构或 dbo.表
                            if (fromName.UsesDoubleDot)
                            {
                                AddTablesInSchema(items, remote, "dbo", settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                                return;
                            }
                            AddDatabaseDotCompletions(items, remote, fromName, connInfo, settings, namePrefix);
                            return;
                        }
                        if (segCount == 3)
                        {
                            // server.db.schema.. 架构已写出，再打 .. 不是合法省略
                            if (fromName.UsesDoubleDot)
                                return;
                            var remote = MetadataCatalogService.Instance.GetOrBuildLinkedCatalog(connInfo, linkedServer, fromName.Segments[1]);
                            AddTablesInSchema(items, remote, fromName.Segments[2], settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                            return;
                        }
                    }
                    if (segCount == 1)
                    {
                        AddLinkedServerDatabases(items, connInfo, linkedServer, fromName, settings);
                        return;
                    }
                    if (segCount == 2 && IsLinkedServerDatabase(connInfo, linkedServer, fromName.Segments[1]))
                    {
                        var remote = MetadataCatalogService.Instance.GetOrBuildLinkedCatalog(connInfo, linkedServer, fromName.Segments[1]);
                        AddDatabaseDotCompletions(items, remote, fromName, connInfo, settings, namePrefix);
                        return;
                    }
                }

                bool qualified = segCount > 0 || fromName.AfterDot;
                if (segCount == 0)
                {
                    // 数据库优先加入，避免被表列表占满 maxCompletionItems
                    AddDatabases(items, connInfo, settings);
                    AddLinkedServers(items, connInfo, settings);
                    AddSchemasFromCatalog(items, catalog, settings, namePrefix ?? fromName?.Partial);
                    AddTablesViewsAndRoutines(items, catalog, settings, includeTableFunctions: true, fromName: fromName, connInfo: connInfo, namePrefix: namePrefix);
                    return;
                }

                if (fromName.AfterDot)
                {
                    // db.. 在 T-SQL 就是省略 dbo，不依赖库名是否已在缓存列表里
                    if (segCount == 1 && fromName.UsesDoubleDot)
                    {
                        var remote = ResolveDatabaseCatalog(connInfo, catalog, fromName.Segments[0]);
                        AddTablesInSchema(items, remote, "dbo", settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                        return;
                    }
                    if (segCount == 1 && IsDatabasePrefix(connInfo, catalog, fromName.Segments[0]))
                    {
                        var remote = ResolveDatabaseCatalog(connInfo, catalog, fromName.Segments[0]);
                        AddDatabaseDotCompletions(items, remote, fromName, connInfo, settings, namePrefix);
                        return;
                    }
                    if (segCount == 1)
                    {
                        AddTablesInSchema(items, catalog, fromName.Segments[0], settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                        return;
                    }
                    if (segCount == 2)
                    {
                        // RtBase.dbo.xxx：第一段当库名；ContainsDatabase 未就绪时仍可用当前 catalog / 缓存兜底
                        var remote = ResolveDatabaseCatalog(connInfo, catalog, fromName.Segments[0]);
                        if (remote != null)
                        {
                            AddTablesInSchema(items, remote, fromName.Segments[1], settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                            return;
                        }
                        AddTablesInSchema(items, catalog, fromName.Segments[0], settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                        return;
                    }
                }

                if (segCount == 1 && fromName.UsesDoubleDot)
                {
                    var remote = ResolveDatabaseCatalog(connInfo, catalog, fromName.Segments[0]);
                    AddTablesInSchema(items, remote, "dbo", settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                    return;
                }

                if (segCount == 1 && IsDatabasePrefix(connInfo, catalog, fromName.Segments[0]))
                {
                    var remote = ResolveDatabaseCatalog(connInfo, catalog, fromName.Segments[0]);
                    AddDatabaseDotCompletions(items, remote, fromName, connInfo, settings, namePrefix);
                    return;
                }

                if (segCount == 2)
                {
                    var remote = ResolveDatabaseCatalog(connInfo, catalog, fromName.Segments[0]);
                    if (remote != null)
                    {
                        AddTablesInSchema(items, remote, fromName.Segments[1], settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                        return;
                    }
                }

                if (segCount == 1)
                {
                    AddTablesInSchema(items, catalog, fromName.Segments[0], settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                    return;
                }

                AddTablesViewsAndRoutines(items, catalog, settings, includeTableFunctions: true, fromName: fromName, connInfo: connInfo, namePrefix: namePrefix);
                if (!qualified)
                    AddDatabases(items, connInfo, settings);
            }

            /// <summary>
            /// db.schema.. / server.db.schema..：架构已经写出后再打 .. 不是合法省略，不应再出对象补全。
            /// db.. / server.db.. 仍是省略 dbo，保持提示。
            /// db..table 后的别名/WHERE 前缀：对象名已写完（AfterDot=false），不要当成 schema..。
            /// </summary>
            private bool IsDoubleDotAfterSchema(FromObjectNameContext fromName, ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                if (fromName == null || !fromName.UsesDoubleDot) return false;
                // 仅「点后还在继续写」时才算 schema..（dbo.| 后的 ..）。
                // db..FJ_Daitui_Items w 的 Segments 也是 [库, 表]，但 AfterDot=false。
                if (!fromName.AfterDot) return false;
                var segs = fromName.Segments;
                int n = segs == null ? 0 : segs.Count;
                if (n <= 1) return false;
                int dbIndex;
                if (CountLeadingIpv4Octets(segs) == 4)
                    dbIndex = 4;
                else if (IsLinkedServerName(connInfo, segs[0]) || IsCompleteIpv4(StripNumericDots(segs[0])))
                    dbIndex = 1;
                else
                    dbIndex = 0;
                return n > dbIndex + 1;
            }

            private static int CountLeadingIpv4Octets(List<string> segs)
            {
                if (segs == null || segs.Count < 4) return 0;
                for (int i = 0; i < 4; i++)
                {
                    string s = segs[i];
                    if (string.IsNullOrEmpty(s) || s.Length > 3) return 0;
                    for (int j = 0; j < s.Length; j++)
                    {
                        if (!char.IsDigit(s[j])) return 0;
                    }
                }
                return 4;
            }

            /// <summary>
            /// 按库名取元数据：优先缓存；若与当前 catalog 库名相同则直接复用（跨库前缀写当前库时常见）。
            /// </summary>
            private static MetadataCatalog ResolveDatabaseCatalog(
                ScriptFactoryAccess.ConnectionInfo connInfo, MetadataCatalog current, string database)
            {
                if (string.IsNullOrEmpty(database)) return current;
                if (current != null
                    && string.Equals(current.Database, database, StringComparison.OrdinalIgnoreCase))
                    return current;
                return GetCatalogNonBlocking(connInfo, database);
            }

            /// <summary>
            /// 库名后一个点：提示架构（dbo 优先）以及 dbo.表。选架构后再点则只出表名。
            /// </summary>
            private void AddDatabaseDotCompletions(
                List<CompletionItem> items,
                MetadataCatalog catalog,
                FromObjectNameContext fromName,
                ScriptFactoryAccess.ConnectionInfo connInfo,
                IntelliSenseSettings settings,
                string namePrefix)
            {
                AddSchemasFromCatalog(items, catalog, settings, namePrefix ?? fromName?.Partial);
                AddTablesInSchema(items, catalog, null, settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
            }

            /// <summary>ContainsDatabase 未就绪时，用已缓存目录判断第一段是否是库名，避免把 jichushuju. 当成当前库的架构。</summary>
            private bool IsDatabasePrefix(ScriptFactoryAccess.ConnectionInfo connInfo, MetadataCatalog current, string name)
            {
                if (string.IsNullOrEmpty(name)) return false;
                if (IsLinkedServerName(connInfo, name)) return false;
                if (IsDatabaseName(connInfo, name)) return true;
                if (current != null && string.Equals(current.Database, name, StringComparison.OrdinalIgnoreCase))
                    return true;
                var cached = MetadataCatalogService.Instance.GetCachedCatalogOrDisk(connInfo, name);
                return cached != null && string.Equals(cached.Database, name, StringComparison.OrdinalIgnoreCase);
            }

            private void AddSchemasFromCatalog(List<CompletionItem> items, MetadataCatalog catalog, IntelliSenseSettings settings, string namePrefix)
            {
                var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool includeSystem = settings == null || settings.includeSystemObjects;
                bool fromServer = catalog != null && catalog.Schemas != null && catalog.Schemas.Count > 0;
                if (fromServer && includeSystem)
                {
                    foreach (var s in catalog.Schemas)
                    {
                        if (!string.IsNullOrEmpty(s)) schemas.Add(s);
                    }
                }
                else if (catalog != null)
                {
                    // 旧缓存没有 Schemas，或关闭了系统对象：从用户对象反推架构
                    if (catalog.Tables != null)
                    {
                        foreach (var t in catalog.Tables)
                        {
                            if (!string.IsNullOrEmpty(t.Schema)) schemas.Add(t.Schema);
                        }
                    }
                    if (catalog.Views != null)
                    {
                        foreach (var v in catalog.Views)
                        {
                            if (!string.IsNullOrEmpty(v.Schema)) schemas.Add(v.Schema);
                        }
                    }
                    if (catalog.ScalarFunctions != null)
                    {
                        foreach (var f in catalog.ScalarFunctions)
                        {
                            if (!string.IsNullOrEmpty(f.Schema)) schemas.Add(f.Schema);
                        }
                    }
                    if (catalog.Procedures != null)
                    {
                        foreach (var p in catalog.Procedures)
                        {
                            if (!string.IsNullOrEmpty(p.Schema)) schemas.Add(p.Schema);
                        }
                    }
                    if (catalog.Synonyms != null)
                    {
                        foreach (var syn in catalog.Synonyms)
                        {
                            if (!string.IsNullOrEmpty(syn.Schema)) schemas.Add(syn.Schema);
                        }
                    }
                }
                if (includeSystem && !fromServer)
                {
                    foreach (var s in MetadataCatalogService.CommonSystemSchemas)
                        schemas.Add(s);
                }
                if (schemas.Count == 0) return;
                string filter = GetLastSegment(namePrefix);
                string dbLabel = catalog != null ? (catalog.Database ?? string.Empty) : string.Empty;
                foreach (var s in schemas
                    .OrderBy(x => string.Equals(x, "dbo", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (!ObjectNameMatchesFilter(s, filter)) continue;
                    items.Add(new CompletionItem(s, FormatIdentifier(s, settings), CompletionKind.Schema,
                        string.IsNullOrEmpty(dbLabel) ? "架构" : "架构 (" + dbLabel + ")"));
                }
            }

            private void AddTablesInSchema(List<CompletionItem> items, MetadataCatalog catalog, string schema, IntelliSenseSettings settings, FromObjectNameContext fromName, ScriptFactoryAccess.ConnectionInfo connInfo, bool includeSystemObjects, string namePrefix = null)
            {
                if (catalog == null)
                {
                    AddSystemCatalogObjects(items, fromName, schema, namePrefix, settings, null, connInfo);
                    return;
                }
                string filter = GetLastSegment(namePrefix ?? fromName?.Partial);
                int cap = Math.Max(PreSortCandidateFloor, settings.maxCompletionItems * PreSortCandidateMultiplier);
                // 插入文本策略只算一次，避免每张表都 IsDatabaseName/连库
                var insertMode = ResolveTableInsertMode(fromName, connInfo, catalog);
                bool tableNameOnly = ShouldDisplayTableNameOnly(fromName, connInfo);
                bool matchQualified = string.IsNullOrEmpty(schema) && insertMode == TableInsertMode.SchemaAndTable;
                int added = 0;
                foreach (var t in catalog.Tables)
                {
                    if (IsSystemSchemaName(t.Schema)) continue;
                    if (!string.IsNullOrEmpty(schema) && !string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!ObjectNameMatchesFilter(t.Name, filter)
                        && !(matchQualified && ObjectNameMatchesFilter(t.QualifiedName, filter)))
                        continue;
                    string insert = BuildTableInsertText(fromName, t, connInfo, settings, insertMode);
                    string display = tableNameOnly ? t.Name : t.QualifiedName;
                    items.Add(new CompletionItem(display, insert, CompletionKind.Table, BuildTableDescription(t)));
                    if (++added >= cap) break;
                }
                if (added < cap)
                {
                    foreach (var v in catalog.Views)
                    {
                        if (IsSystemSchemaName(v.Schema)) continue;
                        if (!string.IsNullOrEmpty(schema) && !string.Equals(v.Schema, schema, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!ObjectNameMatchesFilter(v.Name, filter)
                            && !(matchQualified && ObjectNameMatchesFilter(v.QualifiedName, filter)))
                            continue;
                        string insert = BuildTableInsertText(fromName, v, connInfo, settings, insertMode);
                        string display = tableNameOnly ? v.Name : v.QualifiedName;
                        items.Add(new CompletionItem(display, insert, CompletionKind.View, BuildTableDescription(v)));
                        if (++added >= cap) break;
                    }
                }
                if (added < cap && catalog.TableFunctions != null)
                {
                    foreach (var f in catalog.TableFunctions)
                    {
                        if (IsSystemSchemaName(f.Schema)) continue;
                        if (!string.IsNullOrEmpty(schema) && !string.Equals(f.Schema, schema, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!ObjectNameMatchesFilter(f.Name, filter)
                            && !(matchQualified && ObjectNameMatchesFilter(f.QualifiedName, filter)))
                            continue;
                        string insert = BuildTableInsertText(fromName, f, connInfo, settings, insertMode);
                        insert = AppendFunctionCallParens(insert, out int cursor);
                        string display = tableNameOnly ? f.Name : f.QualifiedName;
                        var item = new CompletionItem(display, insert, CompletionKind.TableFunction, BuildRoutineDescription(f));
                        item.SnippetCursorOffset = cursor;
                        items.Add(item);
                        if (++added >= cap) break;
                    }
                }
                AddSystemCatalogObjects(items, fromName, schema, namePrefix, settings, catalog, connInfo);
            }

            private static bool ObjectNameMatchesFilter(string objectName, string filter)
            {
                if (string.IsNullOrEmpty(filter)) return true;
                if (string.IsNullOrEmpty(objectName)) return false;
                if (objectName.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) return true;
                if (objectName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                string collapsedName = CollapseForMatch(objectName);
                string collapsedFilter = CollapseForMatch(filter);
                if (!string.IsNullOrEmpty(collapsedFilter)
                    && collapsedName.IndexOf(collapsedFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                int[] unused;
                return TryMatchSubsequence(objectName, filter, out unused);
            }

            private enum TableInsertMode
            {
                FullQualified,
                TableOnly,
                SchemaAndTable,
                DbDoubleDotTable,
                DbDotSchemaTable,
                DbDotSchemaAndTable,
                SchemaDotTable
            }

            private TableInsertMode ResolveTableInsertMode(FromObjectNameContext fromName, ScriptFactoryAccess.ConnectionInfo connInfo, MetadataCatalog catalog = null)
            {
                if (fromName == null || (!fromName.InFromClause && !fromName.InSelectList))
                    return TableInsertMode.FullQualified;
                int segCount = fromName.Segments?.Count ?? 0;
                bool linkedServerContext = segCount > 0 && IsLinkedServerName(connInfo, fromName.Segments[0]);
                if (fromName.AfterDot || fromName.PartialStartOffset >= 0)
                {
                    if (linkedServerContext)
                    {
                        if (fromName.UsesDoubleDot || segCount >= 3) return TableInsertMode.TableOnly;
                        if (segCount == 2) return TableInsertMode.SchemaAndTable;
                        return TableInsertMode.TableOnly;
                    }
                    if (fromName.UsesDoubleDot) return TableInsertMode.TableOnly;
                    if (segCount >= 2) return TableInsertMode.TableOnly;
                    if (segCount == 1)
                    {
                        if (IsDatabasePrefix(connInfo, catalog, fromName.Segments[0]))
                            return TableInsertMode.SchemaAndTable;
                        return TableInsertMode.TableOnly;
                    }
                    return TableInsertMode.FullQualified;
                }
                if (segCount == 0) return TableInsertMode.FullQualified;
                string dbOrSchema = fromName.Segments[0];
                bool isDb = IsDatabasePrefix(connInfo, catalog, dbOrSchema);
                if (isDb && fromName.UsesDoubleDot) return TableInsertMode.DbDoubleDotTable;
                if (isDb && segCount >= 2) return TableInsertMode.DbDotSchemaTable;
                if (isDb) return TableInsertMode.DbDotSchemaAndTable;
                if (segCount == 1) return TableInsertMode.SchemaDotTable;
                return TableInsertMode.FullQualified;
            }

            /// <summary>库.dbo. / server.db.dbo. / db.. 只显示表名；当前库 dbo. 仍显示 dbo.表（与既有列表一致）。</summary>
            private bool ShouldDisplayTableNameOnly(FromObjectNameContext fromName, ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                if (fromName == null || !fromName.InFromClause) return false;
                if (fromName.UsesDoubleDot) return true;
                int segCount = fromName.Segments?.Count ?? 0;
                if (segCount == 0) return false;
                if (IsLinkedServerName(connInfo, fromName.Segments[0]))
                    return segCount >= 3;
                return segCount >= 2;
            }

            private void AddSnippets(List<CompletionItem> items, string prefix)
            {
                if (!SettingsManager.GetUseSnippets()) return;

                var snippetSettings = SettingsManager.GetSnippetSettings();
                string marker = snippetSettings.cursorMarker ?? "#";
                foreach (var snippet in SnippetService.GetAllSnippets())
                {
                    if (string.IsNullOrEmpty(snippet.Prefix)) continue;
                    var processed = SnippetVariableProcessor.ProcessVariables(snippet.Body, marker);
                    string desc = string.IsNullOrEmpty(snippet.Description)
                        ? "代码片段"
                        : "片段: " + snippet.Description;
                    var item = new CompletionItem(snippet.Prefix, processed.ProcessedText, CompletionKind.Snippet, desc)
                    {
                        SnippetCursorOffset = processed.CursorOffset,
                        SnippetPrefix = snippet.Prefix
                    };
                    items.Add(item);
                }
            }

            private void AddKeywords(List<CompletionItem> items, IEnumerable<string> keywords)
            {
                foreach (var kw in keywords)
                {
                    items.Add(new CompletionItem(kw, kw, CompletionKind.Keyword, "关键字"));
                }
            }

            private void AddLocalFromObjects(List<CompletionItem> items, LocalSymbols local, IntelliSenseSettings settings, string namePrefix)
            {
                if (local == null) return;
                string filter = GetLastSegment(namePrefix);
                if (local.Ctes != null)
                {
                    foreach (var cte in local.Ctes)
                    {
                        if (string.IsNullOrEmpty(cte.Name) || !ObjectNameMatchesFilter(cte.Name, filter)) continue;
                        items.Add(new CompletionItem(cte.Name, cte.Name, CompletionKind.Table, "CTE"));
                    }
                }
                if (settings != null && settings.includeLocalTempTables && local.TempTables != null)
                {
                    foreach (var tt in local.TempTables)
                    {
                        if (string.IsNullOrEmpty(tt.Name) || !ObjectNameMatchesFilter(tt.Name, filter)) continue;
                        items.Add(new CompletionItem(tt.Name, tt.Name, CompletionKind.Table, "临时表"));
                    }
                }
                if (settings != null && settings.includeLocalVariables && local.TableVariables != null)
                {
                    foreach (var tv in local.TableVariables)
                    {
                        if (string.IsNullOrEmpty(tv.Name) || !ObjectNameMatchesFilter(tv.Name, filter)) continue;
                        items.Add(new CompletionItem(tv.Name, tv.Name, CompletionKind.Variable, "表变量"));
                    }
                }
            }

            private void AddTablesViewsAndRoutines(List<CompletionItem> items, MetadataCatalog catalog, IntelliSenseSettings settings, bool includeTableFunctions, FromObjectNameContext fromName = null, ScriptFactoryAccess.ConnectionInfo connInfo = null, string namePrefix = null)
            {
                AddTablesAndViews(items, catalog, settings, fromName, connInfo, namePrefix);
                if (catalog == null) return;
                string filter = GetLastSegment(namePrefix ?? fromName?.Partial);
                var insertMode = ResolveTableInsertMode(fromName, connInfo, catalog);
                foreach (var s in catalog.Synonyms)
                {
                    if (!ObjectNameMatchesFilter(s.Name, filter)) continue;
                    items.Add(new CompletionItem(s.QualifiedName, FormatObjectInsert(s, settings), CompletionKind.Synonym, "同义词"));
                }
                if (includeTableFunctions)
                {
                    foreach (var f in catalog.TableFunctions)
                    {
                        if (IsSystemSchemaName(f.Schema)) continue;
                        if (!ObjectNameMatchesFilter(f.Name, filter)) continue;
                        string insert = fromName != null ? BuildTableInsertText(fromName, f, connInfo, settings, insertMode) : FormatObjectInsert(f, settings);
                        insert = AppendFunctionCallParens(insert, out int cursor);
                        var item = new CompletionItem(f.QualifiedName, insert, CompletionKind.TableFunction,
                            BuildRoutineDescription(f));
                        item.SnippetCursorOffset = cursor;
                        items.Add(item);
                    }
                }
            }

            private void AddTablesAndViews(List<CompletionItem> items, MetadataCatalog catalog, IntelliSenseSettings settings, FromObjectNameContext fromName = null, ScriptFactoryAccess.ConnectionInfo connInfo = null, string namePrefix = null)
            {
                if (catalog == null)
                {
                    AddSystemCatalogObjects(items, fromName, null, namePrefix, settings, null, connInfo);
                    return;
                }
                string filter = GetLastSegment(namePrefix ?? fromName?.Partial);
                int cap = Math.Max(PreSortCandidateFloor, settings.maxCompletionItems * PreSortCandidateMultiplier);
                var insertMode = ResolveTableInsertMode(fromName, connInfo, catalog);
                int added = 0;
                foreach (var t in catalog.Tables)
                {
                    if (IsSystemSchemaName(t.Schema)) continue;
                    if (!ObjectNameMatchesFilter(t.Name, filter)) continue;
                    string insert = fromName != null ? BuildTableInsertText(fromName, t, connInfo, settings, insertMode) : FormatObjectInsert(t, settings);
                    items.Add(new CompletionItem(t.QualifiedName, insert, CompletionKind.Table,
                        BuildTableDescription(t)));
                    if (++added >= cap) break;
                }
                if (added < cap)
                {
                    foreach (var v in catalog.Views)
                    {
                        if (IsSystemSchemaName(v.Schema)) continue;
                        if (!ObjectNameMatchesFilter(v.Name, filter)) continue;
                        string insert = fromName != null ? BuildTableInsertText(fromName, v, connInfo, settings, insertMode) : FormatObjectInsert(v, settings);
                        items.Add(new CompletionItem(v.QualifiedName, insert, CompletionKind.View,
                            BuildTableDescription(v)));
                        if (++added >= cap) break;
                    }
                }
                AddSystemCatalogObjects(items, fromName, null, namePrefix, settings, catalog, connInfo);
            }

            private void AddColumnsAndFunctions(List<CompletionItem> items, MetadataCatalog catalog, LocalSymbols local, IntelliSenseSettings settings, ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                // 列：来自 FROM 别名表 + CTE + 临时表 + 表变量
                AddColumnsFromFromAliases(items, catalog, local, settings, connInfo);
                AddLocalColumns(items, local, settings);
                // IDEA 风格：展开全部列（逗号分隔）
                AddAllColumnsCompletions(items, catalog, local, settings, connInfo, withAliasPrefix: true);
                // 星号
                items.Add(new CompletionItem("*", "*", CompletionKind.Column, "所有列"));
                // 内建函数与关键字解耦：关闭关键字提示时仍可补 ISNULL/CAST 等
                AddBuiltInFunctions(items);
                if (settings.includeKeywords)
                    AddKeywords(items, new[] { "CASE" });
            }

            /// <summary>
            /// 为每张可解析表生成「全部列」候选项（显示截断列表，插入完整逗号分隔列名）。
            /// </summary>
            private void AddAllColumnsCompletions(
                List<CompletionItem> items,
                MetadataCatalog catalog,
                LocalSymbols local,
                IntelliSenseSettings settings,
                ScriptFactoryAccess.ConnectionInfo connInfo,
                bool withAliasPrefix)
            {
                if (items == null || local?.Aliases == null || local.Aliases.Count == 0) return;
                var preferredAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var tables = new Dictionary<string, TableRef>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in local.Aliases)
                {
                    var tref = kv.Value;
                    if (tref == null || string.IsNullOrEmpty(tref.Name)) continue;
                    string tableKey = (tref.LinkedServer ?? string.Empty) + "|" + (tref.Database ?? string.Empty) + "|" + (tref.Schema ?? string.Empty) + "|" + tref.Name;
                    tables[tableKey] = tref;
                    string aliasKey = kv.Key;
                    if (string.IsNullOrEmpty(aliasKey) || aliasKey.IndexOf('.') >= 0) continue;
                    if (string.Equals(aliasKey, tref.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    string existing;
                    if (!preferredAlias.TryGetValue(tableKey, out existing) || aliasKey.Length < existing.Length)
                        preferredAlias[tableKey] = aliasKey;
                }
                foreach (var kv in tables)
                {
                    var tcol = ResolveTableRef(connInfo, catalog, kv.Value);
                    if (tcol?.Columns == null || tcol.Columns.Count == 0) continue;
                    string alias = null;
                    if (withAliasPrefix)
                        preferredAlias.TryGetValue(kv.Key, out alias);
                    var insertParts = new List<string>(tcol.Columns.Count);
                    var displayParts = new List<string>(tcol.Columns.Count);
                    foreach (var col in tcol.Columns)
                    {
                        if (col == null || string.IsNullOrEmpty(col.Name)) continue;
                        string colInsert = FormatColumnInsert(col.Name, settings);
                        string insert = string.IsNullOrEmpty(alias)
                            ? colInsert
                            : FormatIdentifier(alias, settings) + "." + colInsert;
                        insertParts.Add(insert);
                        displayParts.Add(col.Name);
                    }
                    if (insertParts.Count == 0) continue;
                    string insertText = string.Join(", ", insertParts);
                    string displayText = TruncateColumnListDisplay(displayParts, 56);
                    string desc = "全部列 (" + insertParts.Count + ")\n选中后展开为逗号分隔的列清单";
                    var item = new CompletionItem(displayText, insertText, CompletionKind.AllColumns, desc);
                    item.SourceDatabase = ResolveSourceDatabase(kv.Value, catalog, connInfo);
                    item.SourceTable = FormatSourceTable(tcol, kv.Value);
                    items.Add(item);
                }
            }

            private static string TruncateColumnListDisplay(List<string> columnNames, int maxLen)
            {
                if (columnNames == null || columnNames.Count == 0) return string.Empty;
                var sb = new StringBuilder();
                for (int i = 0; i < columnNames.Count; i++)
                {
                    string part = columnNames[i];
                    if (i > 0)
                    {
                        if (sb.Length + 2 + part.Length > maxLen)
                        {
                            sb.Append(", ...");
                            break;
                        }
                        sb.Append(", ");
                    }
                    else if (part.Length > maxLen)
                    {
                        sb.Append(part.Substring(0, Math.Max(1, maxLen - 3))).Append("...");
                        break;
                    }
                    sb.Append(part);
                }
                return sb.ToString();
            }

            private void AddBuiltInFunctions(List<CompletionItem> items)
            {
                if (items == null) return;
                foreach (var fn in BuiltInFunctionInfos)
                {
                    var item = new CompletionItem(fn.Name, fn.InsertText, CompletionKind.ScalarFunction, BuildBuiltInFunctionDescription(fn));
                    item.SnippetCursorOffset = fn.CursorOffset;
                    items.Add(item);
                }
            }

            /// <summary>HAVING 提示 GROUP BY 列（不提示未分组列）。</summary>
            private void AddGroupByColumns(List<CompletionItem> items, LocalSymbols local, IntelliSenseSettings settings)
            {
                if (local?.GroupByColumns == null || local.GroupByColumns.Count == 0) return;
                foreach (var col in local.GroupByColumns)
                {
                    items.Add(new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "GROUP BY 列"));
                }
            }

            private static string BuildBuiltInFunctionDescription(BuiltInFunctionInfo fn)
            {
                var sb = new StringBuilder();
                sb.AppendLine("内建函数");
                sb.Append(fn.Signature);
                if (fn.Parameters != null && fn.Parameters.Length > 0)
                {
                    sb.AppendLine().AppendLine().AppendLine("参数:");
                    foreach (var p in fn.Parameters)
                        sb.Append("  ").AppendLine(p);
                }
                return sb.ToString().TrimEnd();
            }

            private void AddColumnsFromFromAliases(List<CompletionItem> items, MetadataCatalog catalog, LocalSymbols local, IntelliSenseSettings settings, ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                // 每张表选一个短别名（非表名、非 schema.table）；无别名则不带前缀
                var preferredAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var tables = new Dictionary<string, TableRef>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in local.Aliases)
                {
                    var tref = kv.Value;
                    if (tref == null || string.IsNullOrEmpty(tref.Name)) continue;
                    string tableKey = (tref.LinkedServer ?? string.Empty) + "|" + (tref.Database ?? string.Empty) + "|" + (tref.Schema ?? string.Empty) + "|" + tref.Name;
                    tables[tableKey] = tref;
                    string aliasKey = kv.Key;
                    if (string.IsNullOrEmpty(aliasKey) || aliasKey.IndexOf('.') >= 0) continue;
                    if (string.Equals(aliasKey, tref.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    string existing;
                    if (!preferredAlias.TryGetValue(tableKey, out existing) || aliasKey.Length < existing.Length)
                        preferredAlias[tableKey] = aliasKey;
                }
                foreach (var kv in tables)
                {
                    var tcol = ResolveTableRef(connInfo, catalog, kv.Value);
                    if (tcol == null) continue;
                    string alias;
                    preferredAlias.TryGetValue(kv.Key, out alias);
                    string sourceHint = BuildColumnSourceHint(alias, kv.Value, tcol, catalog, connInfo);
                    foreach (var col in tcol.Columns)
                    {
                        string colInsert = FormatColumnInsert(col.Name, settings);
                        string insert = string.IsNullOrEmpty(alias)
                            ? colInsert
                            : FormatIdentifier(alias, settings) + "." + colInsert;
                        items.Add(CreateColumnItem(col.Name, insert, col, kv.Value, tcol, catalog, connInfo, sourceHint));
                    }
                }
            }

            /// <summary>WHERE/SET 等处提示 FROM 别名，选中后插入 alias. 便于继续补列。</summary>
            private void AddAliasCompletions(
                List<CompletionItem> items, LocalSymbols local, IntelliSenseSettings settings, string prefix)
            {
                if (local?.Aliases == null || local.Aliases.Count == 0) return;
                string p = UnbracketIdentifier(prefix ?? string.Empty);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in local.Aliases)
                {
                    string alias = kv.Key;
                    if (string.IsNullOrEmpty(alias) || alias.IndexOf('.') >= 0) continue;
                    var tref = kv.Value;
                    if (tref != null && string.Equals(alias, tref.Name, StringComparison.OrdinalIgnoreCase)
                        && HasDistinctAlias(local, tref, alias))
                        continue;
                    if (!seen.Add(alias)) continue;
                    if (!string.IsNullOrEmpty(p)
                        && !alias.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string insert = FormatIdentifier(alias, settings) + ".";
                    string target = tref == null || string.IsNullOrEmpty(tref.Name)
                        ? string.Empty
                        : (string.IsNullOrEmpty(tref.Schema) ? tref.Name : tref.Schema + "." + tref.Name);
                    string desc = string.IsNullOrEmpty(target) ? "表别名" : "表别名 → " + target;
                    items.Add(new CompletionItem(alias, insert, CompletionKind.Schema, desc));
                }
            }

            /// <summary>
            /// 当前前缀是否已明确在输入某表别名（抑制 AND 等）。
            /// 单字母不抑制（a 仍要提示 AND）；完整别名或长度≥2 的别名前缀才抑制。
            /// </summary>
            private static bool PrefixLooksLikeAlias(string prefix, LocalSymbols local)
            {
                if (local?.Aliases == null || string.IsNullOrEmpty(prefix)) return false;
                string p = UnbracketIdentifier(prefix);
                if (string.IsNullOrEmpty(p)) return false;
                foreach (var kv in local.Aliases)
                {
                    string alias = kv.Key;
                    if (string.IsNullOrEmpty(alias) || alias.IndexOf('.') >= 0) continue;
                    var tref = kv.Value;
                    if (tref != null && string.Equals(alias, tref.Name, StringComparison.OrdinalIgnoreCase)
                        && HasDistinctAlias(local, tref, alias))
                        continue;
                    if (alias.Equals(p, StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (p.Length >= 2 && alias.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            /// <summary>表已有不同短别名时，不再把表名本身当补全项（SQL Server 此时必须用别名）。</summary>
            private static bool HasDistinctAlias(LocalSymbols local, TableRef tref, string tableName)
            {
                if (local?.Aliases == null || tref == null || string.IsNullOrEmpty(tableName)) return false;
                foreach (var kv in local.Aliases)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Key.IndexOf('.') >= 0) continue;
                    if (string.Equals(kv.Key, tableName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (SameTableRef(kv.Value, tref)) return true;
                }
                return false;
            }

            private static bool SameTableRef(TableRef a, TableRef b)
            {
                if (a == null || b == null) return false;
                if (!string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(a.LinkedServer ?? string.Empty, b.LinkedServer ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!string.Equals(a.Database ?? string.Empty, b.Database ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    return false;
                string sa = string.IsNullOrEmpty(a.Schema) ? "dbo" : a.Schema;
                string sb = string.IsNullOrEmpty(b.Schema) ? "dbo" : b.Schema;
                return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
            }

            private void AddLocalColumns(List<CompletionItem> items, LocalSymbols local, IntelliSenseSettings settings)
            {
                foreach (var cte in local.Ctes)
                {
                    foreach (var col in cte.ColumnNames)
                    {
                        var item = new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "CTE 列: " + cte.Name);
                        item.DisplaySuffix = "(" + cte.Name + ")";
                        items.Add(item);
                    }
                    if (cte.ColumnNames.Count == 0)
                    {
                        items.Add(new CompletionItem(cte.Name, cte.Name, CompletionKind.Table, "CTE"));
                    }
                }
                if (settings.includeLocalTempTables)
                {
                    foreach (var tt in local.TempTables)
                    {
                        foreach (var col in tt.ColumnNames)
                        {
                            var item = new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "临时表列: " + tt.Name);
                            item.DisplaySuffix = "(" + tt.Name + ")";
                            items.Add(item);
                        }
                    }
                }
                if (settings.includeLocalVariables)
                {
                    foreach (var tv in local.TableVariables)
                    {
                        foreach (var col in tv.ColumnNames)
                        {
                            var item = new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "表变量列: " + tv.Name);
                            item.DisplaySuffix = "(" + tv.Name + ")";
                            items.Add(item);
                        }
                    }
                }
            }

            private void AddExecItems(
                List<CompletionItem> items,
                FromObjectNameContext fromName,
                string prefix,
                MetadataCatalog catalog,
                IntelliSenseSettings settings,
                ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                string database = null;
                string schema = null;
                bool nameOnlyInsert = false;
                // 库.前缀（单点）→ 插入 dbo.name；库.. / 库.dbo. → 只插过程名
                bool insertSchemaWithName = false;
                int segCount = fromName?.Segments?.Count ?? 0;
                if (fromName != null && (fromName.ExcessiveDots || IsDoubleDotAfterSchema(fromName, connInfo)))
                    return;

                // EXEC caiwu..pro / EXEC caiwu.dbo.pro / EXEC caiwu.pro / EXEC pro
                if (fromName != null && (segCount > 0 || fromName.AfterDot))
                {
                    nameOnlyInsert = fromName.AfterDot;
                    if (segCount >= 1 && fromName.UsesDoubleDot)
                    {
                        // db..proc → 默认 dbo，首段即库名（勿再依赖 IsDatabaseName）
                        database = fromName.Segments[0];
                        schema = "dbo";
                    }
                    else if (segCount == 1 && fromName.AfterDot)
                    {
                        if (IsDatabaseName(connInfo, fromName.Segments[0]))
                        {
                            database = fromName.Segments[0];
                            insertSchemaWithName = true;
                        }
                        else
                            schema = fromName.Segments[0];
                    }
                    else if (segCount >= 2)
                    {
                        database = fromName.Segments[0];
                        schema = fromName.Segments[1];
                    }
                    else if (segCount == 1 && !fromName.AfterDot)
                    {
                        AddDatabases(items, connInfo, settings);
                        var cur = ResolveExecCatalog(connInfo, null, catalog);
                        AddProceduresFromCatalog(items, cur, settings, null, false, false);
                        return;
                    }
                }

                MetadataCatalog target = ResolveExecCatalog(connInfo, database, catalog);
                if (target == null)
                    return;

                MetadataCatalog procCatalog = target;
                if (IsSystemSchemaName(schema))
                {
                    var sysCat = GetSystemCatalogForCompletion(connInfo, catalog, fromName);
                    if (sysCat != null && sysCat.HasSystemRoutines())
                        procCatalog = sysCat;
                }
                AddProceduresFromCatalog(items, procCatalog, settings, schema, nameOnlyInsert, insertSchemaWithName);
                if (string.IsNullOrEmpty(database) && !nameOnlyInsert)
                    AddDatabases(items, connInfo, settings);
            }

            /// <summary>EXEC 必须有过程目录：跨库按库名取/建；当前库缓存未好或 Routines 残缺则同步补齐。</summary>
            private static MetadataCatalog ResolveExecCatalog(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string database,
                MetadataCatalog currentCatalog)
            {
                if (connInfo == null) return currentCatalog;
                if (!string.IsNullOrEmpty(database))
                    return MetadataCatalogService.Instance.GetOrBuildCatalog(connInfo, database, requireRoutines: true);
                if (currentCatalog != null && currentCatalog.RoutinesLoaded)
                    return currentCatalog;
                return MetadataCatalogService.Instance.GetOrBuildCatalog(connInfo, null, requireRoutines: true)
                       ?? currentCatalog;
            }

            private void AddProceduresFromCatalog(
                List<CompletionItem> items,
                MetadataCatalog catalog,
                IntelliSenseSettings settings,
                string schemaFilter,
                bool nameOnlyInsert,
                bool insertSchemaWithName = false)
            {
                if (catalog == null) return;
                foreach (var p in catalog.Procedures)
                {
                    if (string.IsNullOrEmpty(schemaFilter) && IsSystemSchemaName(p.Schema))
                        continue;
                    if (!string.IsNullOrEmpty(schemaFilter)
                        && !string.Equals(p.Schema, schemaFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string insert;
                    if (insertSchemaWithName)
                    {
                        string sch = string.IsNullOrEmpty(p.Schema) ? "dbo" : p.Schema;
                        insert = FormatIdentifier(sch, settings) + "." + FormatIdentifier(p.Name, settings);
                    }
                    else if (nameOnlyInsert)
                        insert = FormatIdentifier(p.Name, settings);
                    else
                        insert = FormatObjectInsert(p, settings);
                    int cursorOffset;
                    insert = AppendProcedureParameters(insert, p, out cursorOffset);
                    var item = new CompletionItem(p.Name, insert, CompletionKind.Procedure, BuildRoutineDescription(p));
                    item.SourceDatabase = catalog.Database;
                    item.SourceTable = string.IsNullOrEmpty(p.Schema) ? p.Name : p.Schema + "." + p.Name;
                    item.SnippetCursorOffset = cursorOffset;
                    items.Add(item);
                }
                if (!nameOnlyInsert && !insertSchemaWithName && settings.includeSystemObjects)
                {
                    foreach (var s in MetadataCatalogService.CommonSystemObjects)
                    {
                        if (s.StartsWith("sp_"))
                            items.Add(new CompletionItem(s, s, CompletionKind.Procedure, "系统过程"));
                    }
                }
            }

            /// <summary>
            /// 追加命名参数：@Company = , @Name = , @Out = OUTPUT。
            /// cursorOffset 指向第一个待填值位置（InsertText 内偏移）。
            /// </summary>
            private static string AppendProcedureParameters(string insertBase, RoutineInfo routine, out int cursorOffset)
            {
                cursorOffset = -1;
                if (string.IsNullOrEmpty(insertBase) || routine?.Parameters == null || routine.Parameters.Count == 0)
                    return insertBase;

                var sb = new StringBuilder(insertBase);
                bool first = true;
                foreach (var param in routine.Parameters)
                {
                    if (string.IsNullOrEmpty(param.Name)) continue;
                    if (first)
                    {
                        sb.Append(' ');
                        first = false;
                    }
                    else
                        sb.Append(", ");

                    sb.Append(param.Name);
                    if (param.IsOutput)
                    {
                        sb.Append(" = OUTPUT");
                    }
                    else
                    {
                        sb.Append(" = ");
                        if (cursorOffset < 0)
                            cursorOffset = sb.Length;
                    }
                }
                return sb.ToString();
            }

            private void AddProceduresAndScalarFunctions(List<CompletionItem> items, MetadataCatalog catalog, IntelliSenseSettings settings)
            {
                AddProceduresFromCatalog(items, catalog, settings, null, false);
                AddScalarFunctionsFromCatalog(items, catalog, settings, null, null, null);
            }

            private void AddSelectFunctionItems(
                List<CompletionItem> items,
                FromObjectNameContext fromName,
                MetadataCatalog catalog,
                IntelliSenseSettings settings,
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string namePrefix)
            {
                if (fromName == null) return;
                if (fromName.ExcessiveDots || IsDoubleDotAfterSchema(fromName, connInfo)) return;
                int segCount = fromName.Segments?.Count ?? 0;
                if (fromName.AfterDot)
                {
                    if (segCount == 1 && fromName.UsesDoubleDot)
                    {
                        var remote = ResolveDatabaseCatalog(connInfo, catalog, fromName.Segments[0]);
                        AddScalarFunctionsInSchema(items, remote, "dbo", settings, fromName, connInfo, namePrefix);
                        return;
                    }
                    if (segCount == 1 && IsDatabasePrefix(connInfo, catalog, fromName.Segments[0]))
                    {
                        var remote = ResolveDatabaseCatalog(connInfo, catalog, fromName.Segments[0]);
                        AddSchemasFromCatalog(items, remote, settings, namePrefix ?? fromName.Partial);
                        AddScalarFunctionsInSchema(items, remote, null, settings, fromName, connInfo, namePrefix);
                        return;
                    }
                    if (segCount == 1)
                    {
                        AddScalarFunctionsInSchema(items, catalog, fromName.Segments[0], settings, fromName, connInfo, namePrefix);
                        return;
                    }
                    if (segCount == 2)
                    {
                        var remote = ResolveDatabaseCatalog(connInfo, catalog, fromName.Segments[0]);
                        if (remote != null)
                        {
                            AddScalarFunctionsInSchema(items, remote, fromName.Segments[1], settings, fromName, connInfo, namePrefix);
                            return;
                        }
                        AddScalarFunctionsInSchema(items, catalog, fromName.Segments[0], settings, fromName, connInfo, namePrefix);
                    }
                    return;
                }
                if (segCount == 1 && fromName.UsesDoubleDot)
                {
                    var remote = ResolveDatabaseCatalog(connInfo, catalog, fromName.Segments[0]);
                    AddScalarFunctionsInSchema(items, remote, "dbo", settings, fromName, connInfo, namePrefix);
                    return;
                }
                if (segCount == 1 && IsDatabasePrefix(connInfo, catalog, fromName.Segments[0]))
                {
                    var remote = ResolveDatabaseCatalog(connInfo, catalog, fromName.Segments[0]);
                    AddSchemasFromCatalog(items, remote, settings, namePrefix ?? fromName.Partial);
                    AddScalarFunctionsInSchema(items, remote, null, settings, fromName, connInfo, namePrefix);
                }
            }

            private void AddScalarFunctionsFromCatalog(
                List<CompletionItem> items,
                MetadataCatalog catalog,
                IntelliSenseSettings settings,
                FromObjectNameContext fromName,
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string namePrefix)
            {
                AddScalarFunctionsInSchema(items, catalog, null, settings, fromName, connInfo, namePrefix);
            }

            private void AddScalarFunctionsInSchema(
                List<CompletionItem> items,
                MetadataCatalog catalog,
                string schema,
                IntelliSenseSettings settings,
                FromObjectNameContext fromName,
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string namePrefix)
            {
                if (catalog?.ScalarFunctions == null && (string.IsNullOrEmpty(schema) || !IsSystemSchemaName(schema)))
                    return;
                if (IsSystemSchemaName(schema))
                {
                    var sysCat = GetSystemCatalogForCompletion(connInfo, catalog, fromName);
                    if (sysCat != null && sysCat.HasSystemRoutines())
                        catalog = sysCat;
                }
                if (catalog?.ScalarFunctions == null) return;
                string filter = GetLastSegment(namePrefix ?? fromName?.Partial);
                var insertMode = ResolveTableInsertMode(fromName, connInfo, catalog);
                bool nameOnly = fromName != null && fromName.InSelectList && (fromName.AfterDot || fromName.UsesDoubleDot)
                    && (fromName.Segments?.Count ?? 0) >= 2;
                int cap = Math.Max(PreSortCandidateFloor, settings.maxCompletionItems * PreSortCandidateMultiplier);
                int added = 0;
                foreach (var f in catalog.ScalarFunctions)
                {
                    if (string.IsNullOrEmpty(schema) && IsSystemSchemaName(f.Schema))
                        continue;
                    if (!string.IsNullOrEmpty(schema)
                        && !string.Equals(f.Schema, schema, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!ObjectNameMatchesFilter(f.Name, filter)
                        && !ObjectNameMatchesFilter(f.QualifiedName, filter))
                        continue;
                    string insert;
                    if (fromName != null && fromName.InSelectList)
                        insert = BuildTableInsertText(fromName, f, connInfo, settings, insertMode);
                    else
                        insert = FormatObjectInsert(f, settings);
                    if (nameOnly)
                        insert = FormatIdentifier(f.Name, settings);
                    insert = AppendFunctionCallParens(insert, out int cursor);
                    string display = nameOnly || (fromName != null && fromName.AfterDot) ? f.Name : f.QualifiedName;
                    var item = new CompletionItem(display, insert, CompletionKind.ScalarFunction, BuildRoutineDescription(f));
                    item.SnippetCursorOffset = cursor;
                    item.SourceDatabase = catalog.Database;
                    item.SourceTable = f.QualifiedName;
                    items.Add(item);
                    if (++added >= cap) return;
                }
            }

            private static string AppendFunctionCallParens(string insert, out int cursorOffset)
            {
                cursorOffset = -1;
                if (string.IsNullOrEmpty(insert)) return insert;
                if (insert.EndsWith(")", StringComparison.Ordinal)) return insert;
                cursorOffset = insert.Length + 1;
                return insert + "()";
            }

            private void AddMemberColumns(List<CompletionItem> items, string prefix, MetadataCatalog catalog, LocalSymbols local, IntelliSenseSettings settings, ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                // prefix 形如 "alias." 或 "alias.col"，取 "." 前的标识符
                string owner = GetOwnerBeforeDot(prefix);
                if (string.IsNullOrEmpty(owner)) return;

                // 本地对象优先（含别名 → #tmp / @tv）
                if (AddLocalMemberColumns(items, owner, local, settings))
                    return;
                TableRef tref;
                if (local != null && local.Aliases != null && local.Aliases.TryGetValue(owner, out tref)
                    && tref != null && !string.IsNullOrEmpty(tref.Name)
                    && AddLocalMemberColumns(items, tref.Name, local, settings))
                    return;

                // 别名映射
                if (local != null && local.Aliases != null && local.Aliases.TryGetValue(owner, out tref))
                {
                    var tcol = ResolveTableRef(connInfo, catalog, tref);
                    if (tcol != null)
                    {
                        items.Add(new CompletionItem("*", "*", CompletionKind.Column, "所有列"));
                        string sourceHint = BuildColumnSourceHint(owner, tref, tcol, catalog, connInfo);
                        foreach (var col in tcol.Columns)
                        {
                            items.Add(CreateColumnItem(col.Name, FormatColumnInsert(col.Name, settings), col, tref, tcol, catalog, connInfo, sourceHint));
                        }
                    }
                    return;
                }

                if (catalog == null) return;

                // schema 名 → 列出该 schema 下表
                var tablesOfSchema = new List<(TableColumnInfo Obj, CompletionKind Kind)>();
                tablesOfSchema.AddRange(catalog.Tables.Where(t => string.Equals(t.Schema, owner, StringComparison.OrdinalIgnoreCase)).Select(t => (t, CompletionKind.Table)));
                tablesOfSchema.AddRange(catalog.Views.Where(v => string.Equals(v.Schema, owner, StringComparison.OrdinalIgnoreCase)).Select(v => (v, CompletionKind.View)));
                if (tablesOfSchema.Count > 0)
                {
                    foreach (var entry in tablesOfSchema)
                    {
                        var item = new CompletionItem(entry.Obj.Name, FormatObjectInsert(entry.Obj, settings), entry.Kind, BuildTableDescription(entry.Obj));
                        ApplyObjectSource(item, catalog?.Database, entry.Obj);
                        items.Add(item);
                    }
                    return;
                }
            }

            private void AddLocalMemberColumnsFromPrefix(List<CompletionItem> items, string prefix, LocalSymbols local, IntelliSenseSettings settings)
            {
                string owner = GetOwnerBeforeDot(prefix);
                if (string.IsNullOrEmpty(owner)) return;
                AddLocalMemberColumns(items, owner, local, settings);
            }

            private bool AddLocalMemberColumns(List<CompletionItem> items, string owner, LocalSymbols local, IntelliSenseSettings settings)
            {
                // CTE
                var cte = local.Ctes.FirstOrDefault(c => string.Equals(c.Name, owner, StringComparison.OrdinalIgnoreCase));
                if (cte != null)
                {
                    items.Add(new CompletionItem("*", "*", CompletionKind.Column, "所有列"));
                    foreach (var col in cte.ColumnNames)
                    {
                        var item = new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "CTE 列: " + cte.Name);
                        item.DisplaySuffix = "(" + cte.Name + ")";
                        items.Add(item);
                    }
                    return true;
                }
                // 临时表
                if (settings.includeLocalTempTables)
                {
                    var tt = local.TempTables.FirstOrDefault(t => string.Equals(t.Name, owner, StringComparison.OrdinalIgnoreCase));
                    if (tt != null)
                    {
                        foreach (var col in tt.ColumnNames)
                        {
                            items.Add(new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "临时表列: " + tt.Name));
                        }
                        return true;
                    }
                }
                // 表变量
                if (settings.includeLocalVariables)
                {
                    var tv = local.TableVariables.FirstOrDefault(t => string.Equals(t.Name, owner, StringComparison.OrdinalIgnoreCase));
                    if (tv != null)
                    {
                        foreach (var col in tv.ColumnNames)
                        {
                            items.Add(new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "表变量列: " + tv.Name));
                        }
                        return true;
                    }
                }
                return false;
            }

            private void AddLocalVariables(List<CompletionItem> items, LocalSymbols local, IntelliSenseSettings settings)
            {
                if (!settings.includeLocalVariables) return;
                foreach (var v in local.ScalarVariables)
                {
                    items.Add(new CompletionItem(v, v, CompletionKind.Variable, "局部变量"));
                }
                foreach (var tv in local.TableVariables)
                {
                    items.Add(new CompletionItem(tv.Name, tv.Name, CompletionKind.Variable, "表变量"));
                }
            }

            private void AddDatabases(List<CompletionItem> items, ScriptFactoryAccess.ConnectionInfo connInfo, IntelliSenseSettings settings)
            {
                if (connInfo == null) return;
                try
                {
                    var dbs = MetadataCatalogService.Instance.GetDatabasesCached(connInfo);
                    foreach (var db in dbs)
                    {
                        items.Add(new CompletionItem(db, FormatIdentifier(db, settings), CompletionKind.Database, "数据库"));
                    }
                }
                catch
                {
                    // 取库列表失败 → 静默
                }
            }

            private void AddLinkedServers(List<CompletionItem> items, ScriptFactoryAccess.ConnectionInfo connInfo, IntelliSenseSettings settings, string prefixFilter = null)
            {
                if (connInfo == null) return;
                foreach (var server in MetadataCatalogService.Instance.GetLinkedServers(connInfo))
                {
                    if (!string.IsNullOrEmpty(prefixFilter)
                        && !server.StartsWith(prefixFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    items.Add(new CompletionItem(server, FormatLinkedServerInsert(server), CompletionKind.Database, "链接服务器"));
                }
            }

            private void AddLinkedServerDatabases(
                List<CompletionItem> items,
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string linkedServer,
                FromObjectNameContext fromName,
                IntelliSenseSettings settings)
            {
                if (connInfo == null || string.IsNullOrEmpty(linkedServer)) return;
                foreach (var db in MetadataCatalogService.Instance.GetLinkedServerDatabases(connInfo, linkedServer))
                {
                    string insert = BuildLinkedServerDatabaseInsertText(fromName, db, settings);
                    items.Add(new CompletionItem(db, insert, CompletionKind.Database, "链接服务器数据库 (" + linkedServer + ")"));
                }
            }

            private string BuildLinkedServerDatabaseInsertText(FromObjectNameContext fromName, string database, IntelliSenseSettings settings)
            {
                string db = FormatIdentifier(database, settings);
                string ip = JoinNumericFromSegments(fromName);
                if (ip != null && IsCompleteIpv4(ip))
                    return FormatLinkedServerInsert(ip) + "." + db;
                return db;
            }

            private static bool IsSystemSchemaName(string name)
            {
                if (string.IsNullOrEmpty(name)) return false;
                foreach (var s in MetadataCatalogService.CommonSystemSchemas)
                {
                    if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            /// <summary>
            /// sys / INFORMATION_SCHEMA 对象：仅在已写出架构并打点（sys. / INFORMATION_SCHEMA.）后列出。
            /// 未限定架构时不混入，避免 FROM 空前缀扫全量系统目录。
            /// </summary>
            private void AddSystemCatalogObjects(
                List<CompletionItem> items,
                FromObjectNameContext fromName,
                string schemaFilter,
                string namePrefix,
                IntelliSenseSettings settings,
                MetadataCatalog catalog,
                ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                if (settings == null || !settings.includeSystemObjects) return;
                if (string.IsNullOrEmpty(schemaFilter) || !IsSystemSchemaName(schemaFilter))
                    return;
                if (fromName != null && !fromName.AfterDot)
                    return;
                var sysCatalog = GetSystemCatalogForCompletion(connInfo, catalog, fromName);
                var objectSource = (sysCatalog != null && sysCatalog.HasSystemCatalogObjects())
                    ? sysCatalog
                    : (catalog != null && catalog.HasSystemCatalogObjects() ? catalog : null);
                if (objectSource != null)
                    AddSystemCatalogObjectsFromCatalog(items, objectSource, fromName, schemaFilter, namePrefix, settings, connInfo);
                else
                {
                    string nameFilter = GetLastSegment(namePrefix ?? fromName?.Partial);
                    bool nameOnly = !string.IsNullOrEmpty(schemaFilter)
                        && fromName != null
                        && fromName.AfterDot;
                    foreach (var s in MetadataCatalogService.CommonSystemObjects)
                    {
                        if (string.IsNullOrEmpty(s) || s.StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
                            continue;
                        int dot = s.IndexOf('.');
                        if (dot <= 0) continue;
                        string schema = s.Substring(0, dot);
                        string objName = s.Substring(dot + 1);
                        if (!string.IsNullOrEmpty(schemaFilter)
                            && !string.Equals(schema, schemaFilter, StringComparison.OrdinalIgnoreCase))
                            continue;
                        string matchName = nameOnly ? objName : s;
                        if (!ObjectNameMatchesFilter(matchName, nameFilter)
                            && !ObjectNameMatchesFilter(s, nameFilter)
                            && !ObjectNameMatchesFilter(schema, nameFilter))
                            continue;
                        string display = nameOnly ? objName : s;
                        string insert = nameOnly
                            ? FormatIdentifier(objName, settings)
                            : FormatIdentifier(schema, settings) + "." + FormatIdentifier(objName, settings);
                        items.Add(new CompletionItem(display, insert, CompletionKind.View, "系统对象"));
                    }
                }
                var routineSource = (sysCatalog != null && sysCatalog.HasSystemRoutines()) ? sysCatalog : catalog;
                AddSystemSchemaTableFunctions(items, routineSource, fromName, schemaFilter, namePrefix, settings, connInfo);
            }

            private MetadataCatalog GetSystemCatalogForCompletion(
                ScriptFactoryAccess.ConnectionInfo connInfo,
                MetadataCatalog current,
                FromObjectNameContext fromName)
            {
                string server = null;
                if (fromName?.Segments != null && fromName.Segments.Count > 0
                    && IsLinkedServerName(connInfo, fromName.Segments[0]))
                    server = fromName.Segments[0];
                else if (!string.IsNullOrEmpty(current?.Server))
                    server = current.Server;
                else if (connInfo != null)
                    server = connInfo.ServerName;
                if (string.IsNullOrEmpty(server))
                    return null;
                return MetadataCatalogService.Instance.GetCachedSystemCatalog(server);
            }

            private void AddSystemCatalogObjectsFromCatalog(
                List<CompletionItem> items,
                MetadataCatalog catalog,
                FromObjectNameContext fromName,
                string schemaFilter,
                string namePrefix,
                IntelliSenseSettings settings,
                ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                string nameFilter = GetLastSegment(namePrefix ?? fromName?.Partial);
                bool nameOnly = !string.IsNullOrEmpty(schemaFilter)
                    && fromName != null
                    && fromName.AfterDot;
                var insertMode = ResolveTableInsertMode(fromName, connInfo, catalog);
                bool tableNameOnly = ShouldDisplayTableNameOnly(fromName, connInfo) || nameOnly;
                AddSystemSchemaObjects(items, catalog.Tables, CompletionKind.Table, schemaFilter, nameFilter, nameOnly, tableNameOnly, fromName, connInfo, settings, insertMode);
                AddSystemSchemaObjects(items, catalog.Views, CompletionKind.View, schemaFilter, nameFilter, nameOnly, tableNameOnly, fromName, connInfo, settings, insertMode);
            }

            private void AddSystemSchemaTableFunctions(
                List<CompletionItem> items,
                MetadataCatalog catalog,
                FromObjectNameContext fromName,
                string schemaFilter,
                string namePrefix,
                IntelliSenseSettings settings,
                ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                if (catalog?.TableFunctions == null || catalog.TableFunctions.Count == 0) return;
                string nameFilter = GetLastSegment(namePrefix ?? fromName?.Partial);
                bool nameOnly = !string.IsNullOrEmpty(schemaFilter)
                    && fromName != null
                    && fromName.AfterDot;
                var insertMode = ResolveTableInsertMode(fromName, connInfo, catalog);
                bool tableNameOnly = ShouldDisplayTableNameOnly(fromName, connInfo) || nameOnly;
                foreach (var f in catalog.TableFunctions)
                {
                    if (!IsSystemSchemaName(f.Schema)) continue;
                    if (!string.IsNullOrEmpty(schemaFilter)
                        && !string.Equals(f.Schema, schemaFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string qualified = f.QualifiedName;
                    string matchName = nameOnly ? f.Name : qualified;
                    if (!ObjectNameMatchesFilter(matchName, nameFilter)
                        && !ObjectNameMatchesFilter(qualified, nameFilter)
                        && !ObjectNameMatchesFilter(f.Name, nameFilter)
                        && !ObjectNameMatchesFilter(f.Schema, nameFilter))
                        continue;
                    string display = tableNameOnly ? f.Name : qualified;
                    string insert = fromName != null
                        ? BuildTableInsertText(fromName, f, connInfo, settings, insertMode)
                        : FormatObjectInsert(f, settings);
                    insert = AppendFunctionCallParens(insert, out int cursor);
                    var item = new CompletionItem(display, insert, CompletionKind.TableFunction, BuildRoutineDescription(f));
                    item.SnippetCursorOffset = cursor;
                    items.Add(item);
                }
            }

            private void AddSystemSchemaObjects(
                List<CompletionItem> items,
                List<TableColumnInfo> source,
                CompletionKind kind,
                string schemaFilter,
                string nameFilter,
                bool nameOnly,
                bool tableNameOnly,
                FromObjectNameContext fromName,
                ScriptFactoryAccess.ConnectionInfo connInfo,
                IntelliSenseSettings settings,
                TableInsertMode insertMode)
            {
                if (source == null) return;
                foreach (var obj in source)
                {
                    if (!IsSystemSchemaName(obj.Schema)) continue;
                    if (!string.IsNullOrEmpty(schemaFilter)
                        && !string.Equals(obj.Schema, schemaFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string qualified = obj.QualifiedName;
                    string matchName = nameOnly ? obj.Name : qualified;
                    if (!ObjectNameMatchesFilter(matchName, nameFilter)
                        && !ObjectNameMatchesFilter(qualified, nameFilter)
                        && !ObjectNameMatchesFilter(obj.Name, nameFilter)
                        && !ObjectNameMatchesFilter(obj.Schema, nameFilter))
                        continue;
                    string display = tableNameOnly ? obj.Name : qualified;
                    string insert = fromName != null
                        ? BuildTableInsertText(fromName, obj, connInfo, settings, insertMode)
                        : FormatObjectInsert(obj, settings);
                    items.Add(new CompletionItem(display, insert, kind, "系统对象"));
                }
            }

            private void AddSystemObjects(List<CompletionItem> items)
            {
                AddSystemCatalogObjects(items, null, null, null, new IntelliSenseSettings(), null, null);
            }

            /// <summary>候选保留下限：即使 maxCompletionItems 很小，也至少保留这些候选供后续排序截断。</summary>
            private const int PreSortCandidateFloor = 50;
            /// <summary>候选保留系数：排序截断前最多保留 maxCompletionItems × 此倍数的候选，避免多表 JOIN 时过早截掉后续表列。</summary>
            private const int PreSortCandidateMultiplier = 3;

            private List<CompletionItem> FilterAndSort(List<CompletionItem> items, string prefix, IntelliSenseSettings settings, ScriptFactoryAccess.ConnectionInfo connInfo, FromObjectNameContext fromName)
            {
                var filtered = new List<CompletionItem>();
                string p = (prefix ?? string.Empty);
                string filterPrefix = UnbracketIdentifier(GetLastSegment(p));
                if (IsListingLinkedServerIp(fromName))
                    filterPrefix = JoinNumericFromSegments(fromName);

                // 先收集全部匹配再排序截断，避免目录靠前的弱匹配占满配额、漏掉后面的高分表
                foreach (var item in items)
                {
                    int[] indices = null;
                    if (string.IsNullOrEmpty(filterPrefix) || GetMatchScore(item, filterPrefix, out indices) > 0)
                    {
                        if (!string.IsNullOrEmpty(filterPrefix))
                            item.MatchIndices = indices;
                        filtered.Add(item);
                    }
                }

                filtered.Sort((a, b) =>
                {
                    int scoreA = GetMatchScore(a, filterPrefix);
                    int scoreB = GetMatchScore(b, filterPrefix);
                    if (scoreA != scoreB) return scoreB.CompareTo(scoreA);

                    int kindA = GetKindSortOrder(a.Kind);
                    int kindB = GetKindSortOrder(b.Kind);
                    if (kindA != kindB) return kindA.CompareTo(kindB);

                    bool aDbo = IsDboQualifiedName(a.DisplayText);
                    bool bDbo = IsDboQualifiedName(b.DisplayText);
                    if (aDbo != bDbo) return aDbo ? -1 : 1;

                    bool aSystem = IsSystemObjectName(a.DisplayText);
                    bool bSystem = IsSystemObjectName(b.DisplayText);
                    if (aSystem != bSystem) return aSystem ? 1 : -1;

                    return string.Compare(a.DisplayText, b.DisplayText, StringComparison.OrdinalIgnoreCase);
                });

                if (filtered.Count > settings.maxCompletionItems)
                {
                    int keep = settings.maxCompletionItems;
                    // 有前缀时按分数边界扩展，避免把前缀匹配的列/对象中途切掉（上限 3 倍）
                    if (!string.IsNullOrEmpty(filterPrefix))
                    {
                        int cap = Math.Max(PreSortCandidateFloor, settings.maxCompletionItems * PreSortCandidateMultiplier);
                        if (keep > 0 && keep < filtered.Count)
                        {
                            int boundaryScore = GetMatchScore(filtered[keep - 1], filterPrefix);
                            while (keep < filtered.Count && keep < cap
                                && GetMatchScore(filtered[keep], filterPrefix) == boundaryScore)
                                keep++;
                        }
                    }
                    filtered = filtered.Take(keep).ToList();
                }
                return filtered;
            }

            private static int GetKindSortOrder(CompletionKind kind)
            {
                switch (kind)
                {
                    case CompletionKind.AllColumns: return -4; // 全字段置顶（类似 IDEA）
                    case CompletionKind.Column: return -3;
                    case CompletionKind.Procedure: return -2;
                    case CompletionKind.Database: return -1;
                    case CompletionKind.Schema: return -1; // 库. 后 dbo 须排在表前面
                    case CompletionKind.Keyword: return 0;
                    case CompletionKind.ScalarFunction: return 1; // 有前缀时靠 GetMatchScore；空前缀紧随关键字，避免被表挤掉
                    case CompletionKind.Snippet: return 2;
                    case CompletionKind.Table: return 3;
                    case CompletionKind.View: return 4;
                    case CompletionKind.Synonym: return 5;
                    case CompletionKind.TableFunction: return 6;
                    default: return 10;
                }
            }

            private static bool IsDboQualifiedName(string displayText)
            {
                if (string.IsNullOrEmpty(displayText)) return false;
                return displayText.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase);
            }

            private static bool IsSystemObjectName(string displayText)
            {
                if (string.IsNullOrEmpty(displayText)) return false;
                return displayText.StartsWith("INFORMATION_SCHEMA.", StringComparison.OrdinalIgnoreCase) ||
                       displayText.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) ||
                       displayText.StartsWith("sys.", StringComparison.OrdinalIgnoreCase);
            }

            private static int GetMatchScore(CompletionItem item, string filterPrefix)
            {
                int[] unused;
                return GetMatchScore(item, filterPrefix, out unused);
            }

            private static int GetMatchScore(CompletionItem item, string filterPrefix, out int[] matchIndices)
            {
                matchIndices = null;
                if (string.IsNullOrEmpty(filterPrefix)) return 0;
                string displayText = item?.DisplayText ?? string.Empty;
                if (IsNumericOrIpPrefix(filterPrefix))
                {
                    if (displayText.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = ContiguousMatchIndices(0, filterPrefix.Length);
                        return 100;
                    }
                    return 0;
                }
                // 全字段：空前缀已在外层保留；有前缀时任一列名前缀命中则保留（略低于精确列）
                if (item != null && item.Kind == CompletionKind.AllColumns)
                {
                    if (displayText.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = ContiguousMatchIndices(0, filterPrefix.Length);
                        return 90;
                    }
                    string insert = item.InsertText ?? string.Empty;
                    foreach (var part in insert.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string col = UnbracketIdentifier(GetLastSegment(part.Trim()));
                        if (string.IsNullOrEmpty(col)) continue;
                        if (col.Equals(filterPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            matchIndices = ContiguousMatchIndices(0, Math.Min(filterPrefix.Length, displayText.Length));
                            return 95;
                        }
                        if (col.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            matchIndices = ContiguousMatchIndices(0, Math.Min(filterPrefix.Length, displayText.Length));
                            return 90;
                        }
                    }
                    return 0;
                }
                string name = displayText;
                if (item != null && item.Kind == CompletionKind.Snippet &&
                    !string.IsNullOrEmpty(item.SnippetPrefix))
                {
                    name = item.SnippetPrefix;
                }
                else if (item != null &&
                         (item.Kind == CompletionKind.Table || item.Kind == CompletionKind.View ||
                          item.Kind == CompletionKind.Synonym || item.Kind == CompletionKind.TableFunction ||
                          item.Kind == CompletionKind.Procedure || item.Kind == CompletionKind.ScalarFunction ||
                          item.Kind == CompletionKind.Schema))
                {
                    name = GetMatchableName(name);
                }

                // 表别名精确/前缀命中优先于 AND 等关键字
                if (item != null && item.Kind == CompletionKind.Schema
                    && !string.IsNullOrEmpty(item.Description)
                    && item.Description.StartsWith("表别名", StringComparison.Ordinal))
                {
                    if (name.Equals(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(0, name.Length));
                        return 130;
                    }
                    if (name.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(0, filterPrefix.Length));
                        return 120;
                    }
                }

                if (name.Equals(filterPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(0, name.Length));
                    return item != null && item.Kind == CompletionKind.Snippet ? 110 : 100;
                }
                if (name.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(0, filterPrefix.Length));
                    // 子句关键字略高于 CASE 片段：wh → WHERE 优先于 WHEN
                    if (item != null && item.Kind == CompletionKind.Keyword && IsClauseTrailingKeyword(name))
                        return 105;
                    return 100;
                }
                // sys.tables：前缀 sys 要对整段限定名计分，不能只拿最后一段 tables
                if (!string.Equals(displayText, name, StringComparison.OrdinalIgnoreCase)
                    && displayText.IndexOf('.') >= 0)
                {
                    if (displayText.Equals(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = ContiguousMatchIndices(0, displayText.Length);
                        return 100;
                    }
                    if (displayText.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = ContiguousMatchIndices(0, filterPrefix.Length);
                        return 95;
                    }
                }
                // 多词关键字：in → INNER JOIN、group → GROUP BY
                if (item != null && item.Kind == CompletionKind.Keyword && name.IndexOf(' ') >= 0)
                {
                    int sp = name.IndexOf(' ');
                    string first = sp > 0 ? name.Substring(0, sp) : name;
                    if (first.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(0, filterPrefix.Length));
                        return IsClauseTrailingKeyword(name) ? 100 : 95;
                    }
                }
                // 片段：ss → sess（包含）；仍低于前缀命中，ssf 排在 sess 前
                if (item != null && item.Kind == CompletionKind.Snippet)
                {
                    int snippetAt = name.IndexOf(filterPrefix, StringComparison.OrdinalIgnoreCase);
                    if (snippetAt >= 0)
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(snippetAt, filterPrefix.Length));
                        return 80;
                    }
                    return 0;
                }
                // 关键字/内建函数只做前缀匹配，避免 pr 命中 DATEPART 等缩写误匹配
                if (item != null && (item.Kind == CompletionKind.Keyword
                    || item.Kind == CompletionKind.ScalarFunction))
                    return 0;

                // 下划线段前缀：rt_fenjian ← fe（≥2 即可；单字母仍不做，避免 a 扫到过多段）
                if (filterPrefix.Length >= 2)
                {
                    int segStart = 0;
                    for (int i = 0; i <= name.Length; i++)
                    {
                        if (i < name.Length && name[i] != '_') continue;
                        int segLen = i - segStart;
                        if (segLen > 0
                            && segLen >= filterPrefix.Length
                            && string.Compare(name, segStart, filterPrefix, 0, filterPrefix.Length, StringComparison.OrdinalIgnoreCase) == 0)
                        {
                            matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(segStart, filterPrefix.Length));
                            return 60;
                        }
                        segStart = i + 1;
                    }
                }

                // 短前缀不做缩写/包含匹配，避免 aa 命中 CreateDate、a 命中大量列；
                // 数据库通常很少（十几个），不设此限制。
                if (filterPrefix.Length <= 2
                    && (item == null || item.Kind != CompletionKind.Database))
                    return 0;

                int containsAt = name.IndexOf(filterPrefix, StringComparison.OrdinalIgnoreCase);
                if (containsAt >= 0)
                {
                    matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(containsAt, filterPrefix.Length));
                    return 80;
                }

                string collapsedName = CollapseForMatch(name);
                string collapsedFilter = CollapseForMatch(filterPrefix);
                if (!string.IsNullOrEmpty(collapsedFilter))
                {
                    if (collapsedName.StartsWith(collapsedFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name,
                            MapCollapsedRangeToName(name, 0, collapsedFilter.Length));
                        return 85;
                    }
                    int collapsedAt = collapsedName.IndexOf(collapsedFilter, StringComparison.OrdinalIgnoreCase);
                    if (collapsedAt >= 0)
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name,
                            MapCollapsedRangeToName(name, collapsedAt, collapsedFilter.Length));
                        return 65;
                    }
                    int[] subIndices;
                    if (TryMatchSubsequence(name, filterPrefix, out subIndices))
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name, subIndices);
                        return 50;
                    }
                }

                var segs = name.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
                int searchFrom = 0;
                foreach (var seg in segs)
                {
                    int segAt = name.IndexOf(seg, searchFrom, StringComparison.OrdinalIgnoreCase);
                    if (segAt < 0) segAt = searchFrom;
                    int inner = seg.IndexOf(filterPrefix, StringComparison.OrdinalIgnoreCase);
                    if (inner >= 0)
                    {
                        matchIndices = MapMatchIndicesToDisplay(displayText, name, ContiguousMatchIndices(segAt + inner, filterPrefix.Length));
                        return 40;
                    }
                    searchFrom = segAt + seg.Length;
                }
                return 0;
            }

            private static bool IsClauseTrailingKeyword(string name)
            {
                if (string.IsNullOrEmpty(name)) return false;
                switch (name.ToUpperInvariant())
                {
                    case "WHERE":
                    case "HAVING":
                    case "GROUP BY":
                    case "ORDER BY":
                    case "UNION":
                    case "UNION ALL":
                    case "EXCEPT":
                    case "INTERSECT":
                    case "JOIN":
                    case "INNER JOIN":
                    case "LEFT JOIN":
                    case "RIGHT JOIN":
                    case "FULL JOIN":
                    case "CROSS JOIN":
                    case "LEFT OUTER JOIN":
                    case "RIGHT OUTER JOIN":
                    case "FULL OUTER JOIN":
                    case "CROSS APPLY":
                    case "OUTER APPLY":
                    case "DISTINCT":
                    case "TOP":
                        return true;
                    default:
                        return false;
                }
            }

            private static string CollapseForMatch(string text)
            {
                if (string.IsNullOrEmpty(text)) return string.Empty;
                var sb = new StringBuilder(text.Length);
                foreach (char c in text)
                {
                    if (c != '_' && c != '.') sb.Append(c);
                }
                return sb.ToString();
            }

            /// <summary>按序子序列匹配：filter 字符按出现顺序命中 name（如 ddi / daoitem → DH_DaoHuoItem）。优先选连续段更多的命中，便于高亮。</summary>
            private static bool TryMatchSubsequence(string name, string filter, out int[] indices)
            {
                indices = null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(filter)) return false;
                string compactFilter = CollapseForMatch(filter);
                if (string.IsNullOrEmpty(compactFilter)) return false;

                int[] best = null;
                int bestAdjacent = -1;
                for (int start = 0; start < name.Length; start++)
                {
                    char sc = name[start];
                    if (sc == '_' || sc == '.') continue;
                    if (!CharsEqualIgnoreCase(sc, compactFilter[0])) continue;

                    var acc = new int[compactFilter.Length];
                    acc[0] = start;
                    int fi = 1;
                    for (int i = start + 1; i < name.Length && fi < compactFilter.Length; i++)
                    {
                        char nc = name[i];
                        if (nc == '_' || nc == '.') continue;
                        if (!CharsEqualIgnoreCase(nc, compactFilter[fi])) continue;
                        acc[fi] = i;
                        fi++;
                    }
                    if (fi < compactFilter.Length) continue;
                    int adjacent = CountAdjacentMatches(name, acc);
                    if (adjacent > bestAdjacent)
                    {
                        bestAdjacent = adjacent;
                        best = acc;
                        if (bestAdjacent >= compactFilter.Length - 1) break;
                    }
                }
                if (best == null) return false;
                indices = best;
                return true;
            }

            private static bool CharsEqualIgnoreCase(char a, char b)
            {
                return char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
            }

            private static int CountAdjacentMatches(string name, int[] idx)
            {
                int n = 0;
                for (int i = 1; i < idx.Length; i++)
                {
                    bool adjacent = true;
                    for (int p = idx[i - 1] + 1; p < idx[i]; p++)
                    {
                        char c = name[p];
                        if (c != '_' && c != '.')
                        {
                            adjacent = false;
                            break;
                        }
                    }
                    if (adjacent) n++;
                }
                return n;
            }

            private static int[] ContiguousMatchIndices(int start, int length)
            {
                if (length <= 0) return null;
                var a = new int[length];
                for (int i = 0; i < length; i++) a[i] = start + i;
                return a;
            }

            private static int[] MapCollapsedRangeToName(string name, int collapsedStart, int collapsedLen)
            {
                if (string.IsNullOrEmpty(name) || collapsedLen <= 0) return null;
                var list = new int[collapsedLen];
                int ci = 0;
                int filled = 0;
                for (int i = 0; i < name.Length && filled < collapsedLen; i++)
                {
                    if (name[i] == '_' || name[i] == '.') continue;
                    if (ci >= collapsedStart)
                    {
                        list[filled] = i;
                        filled++;
                    }
                    ci++;
                }
                if (filled < collapsedLen) return null;
                return list;
            }

            private static int[] MapMatchIndicesToDisplay(string displayText, string matchedName, int[] nameIndices)
            {
                if (nameIndices == null || nameIndices.Length == 0) return nameIndices;
                if (string.IsNullOrEmpty(displayText) || string.Equals(displayText, matchedName, StringComparison.OrdinalIgnoreCase))
                    return nameIndices;
                int offset = 0;
                if (!string.IsNullOrEmpty(matchedName) && displayText.Length >= matchedName.Length)
                {
                    if (displayText.EndsWith(matchedName, StringComparison.OrdinalIgnoreCase)
                        && (displayText.Length == matchedName.Length
                            || displayText[displayText.Length - matchedName.Length - 1] == '.'))
                    {
                        offset = displayText.Length - matchedName.Length;
                    }
                    else
                    {
                        int at = displayText.IndexOf(matchedName, StringComparison.OrdinalIgnoreCase);
                        if (at >= 0) offset = at;
                    }
                }
                if (offset == 0) return nameIndices;
                var mapped = new int[nameIndices.Length];
                for (int i = 0; i < nameIndices.Length; i++)
                    mapped[i] = nameIndices[i] + offset;
                return mapped;
            }

            private static int GetMatchScore(string displayText, string filterPrefix)
            {
                return GetMatchScore(new CompletionItem { DisplayText = displayText }, filterPrefix);
            }

            private static string GetMatchableName(string displayText)
            {
                if (string.IsNullOrEmpty(displayText)) return string.Empty;
                int dot = displayText.LastIndexOf('.');
                return dot >= 0 ? displayText.Substring(dot + 1) : displayText;
            }

            private static bool ItemMatchesPrefix(CompletionItem item, string filterPrefix)
            {
                if (string.IsNullOrEmpty(filterPrefix)) return true;
                if (item == null) return false;
                return GetMatchScore(item, filterPrefix) > 0;
            }

            private static bool ItemMatchesPrefix(string displayText, string filterPrefix)
            {
                if (string.IsNullOrEmpty(filterPrefix)) return true;
                if (string.IsNullOrEmpty(displayText)) return false;
                return GetMatchScore(displayText, filterPrefix) > 0;
            }

            private static string GetOwnerBeforeDot(string prefix)
            {
                if (string.IsNullOrEmpty(prefix)) return null;
                int dot = prefix.LastIndexOf('.');
                if (dot < 0) return prefix;
                return prefix.Substring(0, dot);
            }

            private static string GetLastSegment(string prefix)
            {
                if (string.IsNullOrEmpty(prefix)) return string.Empty;
                int dot = prefix.LastIndexOf('.');
                if (dot < 0) return prefix;
                return prefix.Substring(dot + 1);
            }

            #endregion

            #region 描述与常量

            private static CompletionItem CreateColumnItem(
                string display,
                string insert,
                ColumnInfo col,
                TableRef tref,
                TableColumnInfo table,
                MetadataCatalog catalog,
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string sourceHint = null)
            {
                var item = new CompletionItem(display, insert, CompletionKind.Column, BuildColumnDescription(col));
                item.SourceDatabase = ResolveSourceDatabase(tref, catalog, connInfo);
                item.SourceTable = FormatSourceTable(table, tref);
                item.DisplaySuffix = sourceHint ?? BuildColumnSourceHint(null, tref, table, catalog, connInfo);
                return item;
            }

            private static string BuildColumnSourceHint(
                string alias,
                TableRef tref,
                TableColumnInfo table,
                MetadataCatalog catalog,
                ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                if (!string.IsNullOrEmpty(alias)
                    && (tref == null || !string.Equals(alias, tref.Name, StringComparison.OrdinalIgnoreCase)))
                    return "(" + alias + ")";
                string db = ResolveSourceDatabase(tref, catalog, connInfo);
                string schema = table != null && !string.IsNullOrEmpty(table.Schema)
                    ? table.Schema
                    : (tref != null && !string.IsNullOrEmpty(tref.Schema) ? tref.Schema : "dbo");
                string name = table != null && !string.IsNullOrEmpty(table.Name)
                    ? table.Name
                    : tref?.Name;
                if (string.IsNullOrEmpty(name)) return null;
                if (string.IsNullOrEmpty(schema)) schema = "dbo";
                if (string.IsNullOrEmpty(db))
                    return "(" + schema + "." + name + ")";
                return "(" + db + "." + schema + "." + name + ")";
            }

            private static void ApplyObjectSource(CompletionItem item, string database, TableColumnInfo obj)
            {
                if (item == null || obj == null) return;
                item.SourceDatabase = database;
                item.SourceTable = FormatSourceTable(obj, null);
            }

            private static string ResolveSourceDatabase(
                TableRef tref,
                MetadataCatalog catalog,
                ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                if (tref != null && !string.IsNullOrEmpty(tref.Database))
                    return tref.Database;
                if (catalog != null && !string.IsNullOrEmpty(catalog.Database))
                    return catalog.Database;
                return connInfo?.Database;
            }

            private static string FormatSourceTable(TableColumnInfo table, TableRef tref)
            {
                if (table != null && !string.IsNullOrEmpty(table.Name))
                {
                    return string.IsNullOrEmpty(table.Schema)
                        ? table.Name
                        : table.Schema + "." + table.Name;
                }
                if (tref != null && !string.IsNullOrEmpty(tref.Name))
                {
                    return string.IsNullOrEmpty(tref.Schema)
                        ? tref.Name
                        : tref.Schema + "." + tref.Name;
                }
                return null;
            }

            private static string BuildTableDescription(TableColumnInfo t)
            {
                var sb = new StringBuilder();
                sb.Append(t.IsView ? "视图: " : "表: ").AppendLine(t.QualifiedName);
                if (!string.IsNullOrEmpty(t.Description))
                {
                    sb.AppendLine().AppendLine(t.Description);
                }
                sb.Append("列数: ").Append(t.Columns?.Count ?? 0);
                return sb.ToString();
            }

            private static string BuildColumnDescription(ColumnInfo c)
            {
                var sb = new StringBuilder();
                sb.Append(c.DataType);
                sb.Append(c.Nullable ? " NULL" : " NOT NULL");
                if (!string.IsNullOrEmpty(c.DefaultValue))
                {
                    sb.Append(" DEFAULT ").Append(c.DefaultValue);
                }
                if (!string.IsNullOrEmpty(c.Description))
                {
                    sb.AppendLine().AppendLine().Append(c.Description);
                }
                return sb.ToString();
            }

            private static string BuildRoutineDescription(RoutineInfo r)
            {
                var sb = new StringBuilder();
                sb.Append(r.Kind == RoutineKind.Procedure ? "存储过程: " : "函数: ");
                sb.AppendLine(r.QualifiedName);
                if (r.Parameters != null && r.Parameters.Count > 0)
                {
                    sb.AppendLine("参数:");
                    foreach (var p in r.Parameters)
                    {
                        sb.Append("  ").Append(p.Name).Append(" ").Append(p.DataType);
                        if (p.IsOutput) sb.Append(" OUTPUT");
                        if (p.HasDefault && !string.IsNullOrEmpty(p.DefaultValue))
                        {
                            sb.Append(" = ").Append(p.DefaultValue);
                        }
                        sb.AppendLine();
                    }
                }
                if (!string.IsNullOrEmpty(r.Description))
                {
                    sb.AppendLine().AppendLine(r.Description);
                }
                return sb.ToString();
            }

            public static readonly string[] SelectClauseKeywords =
            {
                "DISTINCT", "TOP", "ALL", "PERCENT",
                "AS", "FROM", "INTO",
                "CASE", "WHEN", "THEN", "ELSE", "END",
                "OVER", "PARTITION BY", "ROWS", "RANGE", "UNBOUNDED", "PRECEDING", "FOLLOWING", "CURRENT ROW"
            };

            /// <summary>GROUP/ORDER 后补 BY（含 GROUP BY 扩展）。</summary>
            public static readonly string[] GroupOrderByKeywords = { "BY", "ROLLUP", "CUBE", "GROUPING SETS" };

            /// <summary>CAST/CONVERT/PARSE 的类型参数位置：SQL Server 内置数据类型。</summary>
            public static readonly string[] DataTypeKeywords =
            {
                "bigint", "binary", "bit", "char", "date", "datetime", "datetime2", "datetimeoffset",
                "decimal", "float", "geography", "geometry", "hierarchyid", "image", "int", "money",
                "nchar", "ntext", "numeric", "nvarchar", "real", "rowversion", "smalldatetime", "smallint",
                "smallmoney", "sql_variant", "text", "time", "tinyint", "uniqueidentifier", "varbinary",
                "varchar", "xml"
            };

            /// <summary>INSERT 后常见关键字。</summary>
            public static readonly string[] InsertKeywords = { "INTO", "SELECT", "VALUES", "DEFAULT" };

            /// <summary>表名后 ( 或 WITH ( 的表提示。</summary>
            public static readonly string[] TableHintKeywords = { "NOLOCK", "READUNCOMMITTED", "READPAST" };

            public static readonly string[] TopLevelKeywords =
            {
                "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "DROP",
                "EXEC", "EXECUTE", "USE", "DECLARE", "SET", "IF", "BEGIN", "END", "TRUNCATE", "MERGE", "GO",
                "THROW", "PRINT", "RAISERROR", "WAITFOR", "DBCC", "BACKUP", "RESTORE", "BULK",
                "GRANT", "REVOKE", "DENY", "WHILE", "RETURN",
                "COMMIT", "ROLLBACK", "BREAK", "CONTINUE", "CHECKPOINT", "SAVE"
            };

            /// <summary>FROM 表之后常见子句/连接关键字。</summary>
            public static readonly string[] AfterFromKeywords =
            {
                "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "JOIN",
                "LEFT OUTER JOIN", "RIGHT OUTER JOIN", "FULL OUTER JOIN",
                "CROSS APPLY", "OUTER APPLY",
                "PIVOT", "UNPIVOT", "TABLESAMPLE",
                "WHERE", "GROUP BY", "ORDER BY", "HAVING",
                "UNION", "UNION ALL", "EXCEPT", "INTERSECT"
            };

            /// <summary>WHERE/HAVING/ON 条件之后可接的子句关键字。</summary>
            public static readonly string[] AfterWhereKeywords =
            {
                "WHERE", // ON/JOIN 条件后常见；WHERE 后再输 wh 也会匹配，可接受
                "GROUP BY", "ORDER BY", "HAVING",
                "UNION", "UNION ALL", "EXCEPT", "INTERSECT"
            };

            /// <summary>GROUP BY / ORDER BY 列表之后可接的子句关键字。</summary>
            public static readonly string[] AfterGroupOrderKeywords =
            {
                "HAVING", "ORDER BY", "OPTION",
                "UNION", "UNION ALL", "EXCEPT", "INTERSECT"
            };

            public static readonly string[] CreateObjectKeywords =
            {
                "TABLE", "VIEW", "PROCEDURE", "FUNCTION", "INDEX", "SCHEMA", "TYPE", "TRIGGER",
                "SEQUENCE", "SYNONYM", "DATABASE", "USER", "ROLE", "LOGIN", "DEFAULT", "RULE", "STATISTICS"
            };

            public static readonly string[] AlterObjectKeywords =
            {
                "TABLE", "VIEW", "PROCEDURE", "FUNCTION", "INDEX", "SCHEMA", "TRIGGER",
                "SEQUENCE", "DATABASE", "USER", "ROLE", "LOGIN"
            };

            public static readonly string[] DropObjectKeywords =
            {
                "TABLE", "VIEW", "PROCEDURE", "FUNCTION", "INDEX", "SCHEMA", "TYPE", "TRIGGER",
                "SEQUENCE", "SYNONYM", "DATABASE", "USER", "ROLE", "LOGIN", "STATISTICS", "CONSTRAINT"
            };

            public static readonly string[] OperatorKeywords =
            {
                "AND", "OR", "NOT", "IN", "BETWEEN", "LIKE", "IS", "NULL", "EXISTS", "CASE", "WHEN", "THEN", "ELSE", "END"
            };

            /// <summary>WHERE/HAVING/ON 条件运算符（不含 CASE/WHEN，避免 wh→WHEN 压过 WHERE）。</summary>
            public static readonly string[] WhereOperatorKeywords =
            {
                "AND", "OR", "NOT", "IN", "BETWEEN", "LIKE", "IS", "NULL", "EXISTS"
            };

            public static readonly string[] CaseKeywords =
            {
                "CASE", "WHEN", "THEN", "ELSE", "END"
            };

            private static bool ShouldSuggestAfterFromKeywords(FromObjectNameContext fromName, string prefix)
            {
                // 正在输限定名下一段（dbo.|）时不夹 JOIN/WHERE
                if (fromName != null && fromName.AfterDot) return false;
                if (string.IsNullOrEmpty(prefix)) return false;
                if (fromName == null || !fromName.InFromClause) return false;
                // 已有表名后再提示 JOIN / WHERE（如 FROM dbo.t gr）
                if (fromName.Segments != null && fromName.Segments.Count > 0)
                    return true;
                // JOIN ON 后换行 in|：Segments 可能仍空，但前缀已是 JOIN/WHERE 类
                return IsFromJoinOrClausePrefix(prefix);
            }

            private sealed class BuiltInFunctionInfo
            {
                public string Name;
                public string Signature;
                public string InsertText;
                public int CursorOffset;
                public string[] Parameters;
            }

            /// <summary>内建函数：插入完整括号/参数占位，右侧展示签名。覆盖日常高频标量/聚合/窗口函数。</summary>
            private static readonly BuiltInFunctionInfo[] BuiltInFunctionInfos = BuildBuiltInFunctionInfos();

            private static BuiltInFunctionInfo[] BuildBuiltInFunctionInfos()
            {
                return new[]
                {
                    // —— 聚合 ——
                    Fn("COUNT", "COUNT(expression)", "COUNT()", 6, "expression"),
                    Fn("COUNT_BIG", "COUNT_BIG(expression)", "COUNT_BIG()", 10, "expression"),
                    Fn("SUM", "SUM(expression)", "SUM()", 4, "expression"),
                    Fn("AVG", "AVG(expression)", "AVG()", 4, "expression"),
                    Fn("MIN", "MIN(expression)", "MIN()", 4, "expression"),
                    Fn("MAX", "MAX(expression)", "MAX()", 4, "expression"),
                    Fn("STDEV", "STDEV(expression)", "STDEV()", 6, "expression"),
                    Fn("STDEVP", "STDEVP(expression)", "STDEVP()", 7, "expression"),
                    Fn("VAR", "VAR(expression)", "VAR()", 4, "expression"),
                    Fn("VARP", "VARP(expression)", "VARP()", 5, "expression"),
                    Fn("STRING_AGG", "STRING_AGG(expression, separator)", "STRING_AGG(, )", 11,
                        "expression", "separator"),
                    Fn("GROUPING", "GROUPING(column_expression)", "GROUPING()", 9, "column_expression"),
                    Fn("GROUPING_ID", "GROUPING_ID(column_expression [, ...n])", "GROUPING_ID()", 12,
                        "column_expression"),
                    Fn("CHECKSUM_AGG", "CHECKSUM_AGG(expression)", "CHECKSUM_AGG()", 13, "expression"),
                    Fn("APPROX_COUNT_DISTINCT", "APPROX_COUNT_DISTINCT(expression)", "APPROX_COUNT_DISTINCT()", 21, "expression"),

                    // —— 空值 / 逻辑 ——
                    Fn("ISNULL", "ISNULL(check_expression, replacement_value)", "ISNULL(, )", 7,
                        "check_expression", "replacement_value"),
                    Fn("COALESCE", "COALESCE(expression1, expression2 [, ...n])", "COALESCE(, )", 9,
                        "expression1", "expression2", "..."),
                    Fn("NULLIF", "NULLIF(expression, expression)", "NULLIF(, )", 7,
                        "expression", "expression"),
                    Fn("IIF", "IIF(boolean_expression, true_value, false_value)", "IIF(, , )", 4,
                        "boolean_expression", "true_value", "false_value"),
                    Fn("CHOOSE", "CHOOSE(index, val_1, val_2 [, ...])", "CHOOSE(, )", 7,
                        "index", "val_1", "val_2", "..."),

                    // —— 类型转换 ——
                    Fn("CAST", "CAST(expression AS data_type)", "CAST( AS )", 5,
                        "expression", "data_type"),
                    Fn("CONVERT", "CONVERT(data_type, expression [, style])", "CONVERT(, )", 8,
                        "data_type", "expression", "style (optional)"),
                    Fn("TRY_CAST", "TRY_CAST(expression AS data_type)", "TRY_CAST( AS )", 9,
                        "expression", "data_type"),
                    Fn("TRY_CONVERT", "TRY_CONVERT(data_type, expression [, style])", "TRY_CONVERT(, )", 12,
                        "data_type", "expression", "style (optional)"),
                    Fn("PARSE", "PARSE(string_value AS data_type [USING culture])", "PARSE( AS )", 6,
                        "string_value", "data_type", "culture (optional)"),
                    Fn("TRY_PARSE", "TRY_PARSE(string_value AS data_type [USING culture])", "TRY_PARSE( AS )", 10,
                        "string_value", "data_type", "culture (optional)"),
                    Fn("STR", "STR(float_expression [, length [, decimal]])", "STR()", 4,
                        "float_expression", "length (optional)", "decimal (optional)"),
                    Fn("FORMAT", "FORMAT(value, format [, culture])", "FORMAT(, )", 7,
                        "value", "format", "culture (optional)"),

                    // —— 日期时间 ——
                    Fn("GETDATE", "GETDATE()", "GETDATE()", -1),
                    Fn("GETUTCDATE", "GETUTCDATE()", "GETUTCDATE()", -1),
                    Fn("SYSDATETIME", "SYSDATETIME()", "SYSDATETIME()", -1),
                    Fn("SYSUTCDATETIME", "SYSUTCDATETIME()", "SYSUTCDATETIME()", -1),
                    Fn("SYSDATETIMEOFFSET", "SYSDATETIMEOFFSET()", "SYSDATETIMEOFFSET()", -1),
                    Fn("CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP", -1),
                    Fn("DATEADD", "DATEADD(datepart, number, date)", "DATEADD(, , )", 8,
                        "datepart", "number", "date"),
                    Fn("DATEDIFF", "DATEDIFF(datepart, startdate, enddate)", "DATEDIFF(, , )", 9,
                        "datepart", "startdate", "enddate"),
                    Fn("DATEDIFF_BIG", "DATEDIFF_BIG(datepart, startdate, enddate)", "DATEDIFF_BIG(, , )", 13,
                        "datepart", "startdate", "enddate"),
                    Fn("DATENAME", "DATENAME(datepart, date)", "DATENAME(, )", 9,
                        "datepart", "date"),
                    Fn("DATEPART", "DATEPART(datepart, date)", "DATEPART(, )", 9,
                        "datepart", "date"),
                    Fn("YEAR", "YEAR(date)", "YEAR()", 5, "date"),
                    Fn("MONTH", "MONTH(date)", "MONTH()", 6, "date"),
                    Fn("DAY", "DAY(date)", "DAY()", 4, "date"),
                    Fn("EOMONTH", "EOMONTH(start_date [, month_to_add])", "EOMONTH()", 8,
                        "start_date", "month_to_add (optional)"),
                    Fn("DATEFROMPARTS", "DATEFROMPARTS(year, month, day)", "DATEFROMPARTS(, , )", 14,
                        "year", "month", "day"),
                    Fn("DATETIMEFROMPARTS", "DATETIMEFROMPARTS(year, month, day, hour, minute, seconds, milliseconds)",
                        "DATETIMEFROMPARTS(, , , , , , )", 18,
                        "year", "month", "day", "hour", "minute", "seconds", "milliseconds"),
                    Fn("DATETIME2FROMPARTS", "DATETIME2FROMPARTS(year, month, day, hour, minute, seconds, fractions, precision)",
                        "DATETIME2FROMPARTS(, , , , , , , )", 19,
                        "year", "month", "day", "hour", "minute", "seconds", "fractions", "precision"),
                    Fn("SMALLDATETIMEFROMPARTS", "SMALLDATETIMEFROMPARTS(year, month, day, hour, minute)",
                        "SMALLDATETIMEFROMPARTS(, , , , )", 23,
                        "year", "month", "day", "hour", "minute"),
                    Fn("TIMEFROMPARTS", "TIMEFROMPARTS(hour, minute, seconds, fractions, precision)",
                        "TIMEFROMPARTS(, , , , )", 14,
                        "hour", "minute", "seconds", "fractions", "precision"),
                    Fn("DATETIMEOFFSETFROMPARTS", "DATETIMEOFFSETFROMPARTS(year, month, day, hour, minute, seconds, fractions, hour_offset, minute_offset, precision)",
                        "DATETIMEOFFSETFROMPARTS(, , , , , , , , , )", 24,
                        "year", "month", "day", "hour", "minute", "seconds", "fractions", "hour_offset", "minute_offset", "precision"),
                    Fn("ISDATE", "ISDATE(expression)", "ISDATE()", 7, "expression"),
                    Fn("SWITCHOFFSET", "SWITCHOFFSET(datetimeoffset_expression, timezoneoffset)", "SWITCHOFFSET(, )", 13,
                        "datetimeoffset_expression", "timezoneoffset"),
                    Fn("TODATETIMEOFFSET", "TODATETIMEOFFSET(datetime_expression, timezoneoffset)", "TODATETIMEOFFSET(, )", 17,
                        "datetime_expression", "timezoneoffset"),

                    // —— 字符串 ——
                    Fn("LEN", "LEN(string_expression)", "LEN()", 4, "string_expression"),
                    Fn("DATALENGTH", "DATALENGTH(expression)", "DATALENGTH()", 11, "expression"),
                    Fn("SUBSTRING", "SUBSTRING(expression, start, length)", "SUBSTRING(, , )", 10,
                        "expression", "start", "length"),
                    Fn("LEFT", "LEFT(character_expression, integer_expression)", "LEFT(, )", 5,
                        "character_expression", "integer_expression"),
                    Fn("RIGHT", "RIGHT(character_expression, integer_expression)", "RIGHT(, )", 6,
                        "character_expression", "integer_expression"),
                    Fn("CHARINDEX", "CHARINDEX(expressionToFind, expressionToSearch [, start_location])", "CHARINDEX(, )", 10,
                        "expressionToFind", "expressionToSearch", "start_location (optional)"),
                    Fn("PATINDEX", "PATINDEX('%pattern%', expression)", "PATINDEX(, )", 9,
                        "pattern", "expression"),
                    Fn("REPLACE", "REPLACE(string_expression, string_pattern, string_replacement)", "REPLACE(, , )", 8,
                        "string_expression", "string_pattern", "string_replacement"),
                    Fn("STUFF", "STUFF(character_expression, start, length, replaceWith_expression)", "STUFF(, , , )", 6,
                        "character_expression", "start", "length", "replaceWith_expression"),
                    Fn("UPPER", "UPPER(character_expression)", "UPPER()", 6, "character_expression"),
                    Fn("LOWER", "LOWER(character_expression)", "LOWER()", 6, "character_expression"),
                    Fn("LTRIM", "LTRIM(character_expression)", "LTRIM()", 6, "character_expression"),
                    Fn("RTRIM", "RTRIM(character_expression)", "RTRIM()", 6, "character_expression"),
                    Fn("TRIM", "TRIM([characters FROM] string)", "TRIM()", 5, "string"),
                    Fn("CONCAT", "CONCAT(string_value1, string_value2 [, ...n])", "CONCAT(, )", 7,
                        "string_value1", "string_value2", "..."),
                    Fn("CONCAT_WS", "CONCAT_WS(separator, argument1, argument2 [, ...n])", "CONCAT_WS(, )", 10,
                        "separator", "argument1", "argument2", "..."),
                    Fn("REVERSE", "REVERSE(string_expression)", "REVERSE()", 8, "string_expression"),
                    Fn("REPLICATE", "REPLICATE(string_expression, integer_expression)", "REPLICATE(, )", 10,
                        "string_expression", "integer_expression"),
                    Fn("SPACE", "SPACE(integer_expression)", "SPACE()", 6, "integer_expression"),
                    Fn("CHAR", "CHAR(integer_expression)", "CHAR()", 5, "integer_expression"),
                    Fn("NCHAR", "NCHAR(integer_expression)", "NCHAR()", 6, "integer_expression"),
                    Fn("ASCII", "ASCII(character_expression)", "ASCII()", 6, "character_expression"),
                    Fn("UNICODE", "UNICODE(character_expression)", "UNICODE()", 8, "character_expression"),
                    Fn("QUOTENAME", "QUOTENAME(character_string [, quote_character])", "QUOTENAME()", 9,
                        "character_string", "quote_character (optional)"),
                    Fn("SOUNDEX", "SOUNDEX(character_expression)", "SOUNDEX()", 8, "character_expression"),
                    Fn("DIFFERENCE", "DIFFERENCE(character_expression, character_expression)", "DIFFERENCE(, )", 11,
                        "character_expression", "character_expression"),
                    Fn("TRANSLATE", "TRANSLATE(inputString, characters, translations)", "TRANSLATE(, , )", 10,
                        "inputString", "characters", "translations"),
                    Fn("STRING_ESCAPE", "STRING_ESCAPE(text, type)", "STRING_ESCAPE(, )", 14,
                        "text", "type"),
                    Fn("STRING_SPLIT", "STRING_SPLIT(string, separator)", "STRING_SPLIT(, )", 13,
                        "string", "separator"),

                    // —— 数值 ——
                    Fn("ABS", "ABS(numeric_expression)", "ABS()", 4, "numeric_expression"),
                    Fn("CEILING", "CEILING(numeric_expression)", "CEILING()", 8, "numeric_expression"),
                    Fn("FLOOR", "FLOOR(numeric_expression)", "FLOOR()", 6, "numeric_expression"),
                    Fn("ROUND", "ROUND(numeric_expression, length [, function])", "ROUND(, )", 6,
                        "numeric_expression", "length", "function (optional)"),
                    Fn("POWER", "POWER(float_expression, y)", "POWER(, )", 6,
                        "float_expression", "y"),
                    Fn("SQRT", "SQRT(float_expression)", "SQRT()", 5, "float_expression"),
                    Fn("SQUARE", "SQUARE(float_expression)", "SQUARE()", 7, "float_expression"),
                    Fn("EXP", "EXP(float_expression)", "EXP()", 4, "float_expression"),
                    Fn("LOG", "LOG(float_expression [, base])", "LOG()", 4,
                        "float_expression", "base (optional)"),
                    Fn("LOG10", "LOG10(float_expression)", "LOG10()", 6, "float_expression"),
                    Fn("SIGN", "SIGN(numeric_expression)", "SIGN()", 5, "numeric_expression"),
                    Fn("RAND", "RAND([seed])", "RAND()", 5, "seed (optional)"),
                    Fn("PI", "PI()", "PI()", -1),
                    Fn("SIN", "SIN(float_expression)", "SIN()", 4, "float_expression"),
                    Fn("COS", "COS(float_expression)", "COS()", 4, "float_expression"),
                    Fn("TAN", "TAN(float_expression)", "TAN()", 4, "float_expression"),
                    Fn("ASIN", "ASIN(float_expression)", "ASIN()", 5, "float_expression"),
                    Fn("ACOS", "ACOS(float_expression)", "ACOS()", 5, "float_expression"),
                    Fn("ATAN", "ATAN(float_expression)", "ATAN()", 5, "float_expression"),
                    Fn("ATN2", "ATN2(float_expression, float_expression)", "ATN2(, )", 5,
                        "float_expression", "float_expression"),
                    Fn("COT", "COT(float_expression)", "COT()", 4, "float_expression"),
                    Fn("DEGREES", "DEGREES(numeric_expression)", "DEGREES()", 8, "numeric_expression"),
                    Fn("RADIANS", "RADIANS(numeric_expression)", "RADIANS()", 8, "numeric_expression"),
                    Fn("ISNUMERIC", "ISNUMERIC(expression)", "ISNUMERIC()", 10, "expression"),
                    Fn("CHECKSUM", "CHECKSUM(expression [, ...n])", "CHECKSUM()", 9, "expression"),
                    Fn("HASHBYTES", "HASHBYTES(algorithm, expression)", "HASHBYTES(, )", 10,
                        "algorithm", "expression"),

                    // —— JSON ——
                    Fn("ISJSON", "ISJSON(expression [, json_path])", "ISJSON()", 7,
                        "expression", "json_path (optional)"),
                    Fn("JSON_VALUE", "JSON_VALUE(expression, path)", "JSON_VALUE(, )", 11,
                        "expression", "path"),
                    Fn("JSON_QUERY", "JSON_QUERY(expression [, path])", "JSON_QUERY()", 11,
                        "expression", "path (optional)"),
                    Fn("JSON_MODIFY", "JSON_MODIFY(expression, path, newValue)", "JSON_MODIFY(, , )", 12,
                        "expression", "path", "newValue"),

                    // —— 窗口 ——
                    Fn("ROW_NUMBER", "ROW_NUMBER() OVER (ORDER BY ...)", "ROW_NUMBER() OVER (ORDER BY )", 27),
                    Fn("RANK", "RANK() OVER (ORDER BY ...)", "RANK() OVER (ORDER BY )", 21),
                    Fn("DENSE_RANK", "DENSE_RANK() OVER (ORDER BY ...)", "DENSE_RANK() OVER (ORDER BY )", 27),
                    Fn("NTILE", "NTILE(integer_expression) OVER (ORDER BY ...)", "NTILE() OVER (ORDER BY )", 6,
                        "integer_expression"),
                    Fn("LEAD", "LEAD(scalar_expression [, offset [, default]]) OVER (ORDER BY ...)", "LEAD() OVER (ORDER BY )", 5,
                        "scalar_expression", "offset (optional)", "default (optional)"),
                    Fn("LAG", "LAG(scalar_expression [, offset [, default]]) OVER (ORDER BY ...)", "LAG() OVER (ORDER BY )", 4,
                        "scalar_expression", "offset (optional)", "default (optional)"),
                    Fn("FIRST_VALUE", "FIRST_VALUE(scalar_expression) OVER (ORDER BY ...)", "FIRST_VALUE() OVER (ORDER BY )", 12,
                        "scalar_expression"),
                    Fn("LAST_VALUE", "LAST_VALUE(scalar_expression) OVER (ORDER BY ...)", "LAST_VALUE() OVER (ORDER BY )", 11,
                        "scalar_expression"),
                    Fn("PERCENT_RANK", "PERCENT_RANK() OVER (ORDER BY ...)", "PERCENT_RANK() OVER (ORDER BY )", 29),
                    Fn("CUME_DIST", "CUME_DIST() OVER (ORDER BY ...)", "CUME_DIST() OVER (ORDER BY )", 26),
                    Fn("NTH_VALUE", "NTH_VALUE(expression, n) OVER (ORDER BY ...)", "NTH_VALUE(, ) OVER (ORDER BY )", 11,
                        "expression", "n"),
                    Fn("PERCENTILE_CONT", "PERCENTILE_CONT(numeric_literal) WITHIN GROUP (ORDER BY expression) OVER (...)",
                        "PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY ) OVER ()", 15,
                        "numeric_literal", "expression"),
                    Fn("PERCENTILE_DISC", "PERCENTILE_DISC(numeric_literal) WITHIN GROUP (ORDER BY expression) OVER (...)",
                        "PERCENTILE_DISC(0.5) WITHIN GROUP (ORDER BY ) OVER ()", 15,
                        "numeric_literal", "expression"),

                    // —— 标识 / 系统信息 ——
                    Fn("NEWID", "NEWID()", "NEWID()", -1),
                    Fn("NEWSEQUENTIALID", "NEWSEQUENTIALID()", "NEWSEQUENTIALID()", -1),
                    Fn("SCOPE_IDENTITY", "SCOPE_IDENTITY()", "SCOPE_IDENTITY()", -1),
                    Fn("IDENT_CURRENT", "IDENT_CURRENT(table_name)", "IDENT_CURRENT()", 14, "table_name"),
                    Fn("IDENT_INCR", "IDENT_INCR(table_or_view)", "IDENT_INCR()", 11, "table_or_view"),
                    Fn("IDENT_SEED", "IDENT_SEED(table_or_view)", "IDENT_SEED()", 11, "table_or_view"),
                    Fn("@@ROWCOUNT", "@@ROWCOUNT", "@@ROWCOUNT", -1),
                    Fn("@@IDENTITY", "@@IDENTITY", "@@IDENTITY", -1),
                    Fn("@@ERROR", "@@ERROR", "@@ERROR", -1),
                    Fn("@@TRANCOUNT", "@@TRANCOUNT", "@@TRANCOUNT", -1),
                    Fn("@@SERVERNAME", "@@SERVERNAME", "@@SERVERNAME", -1),
                    Fn("@@SPID", "@@SPID", "@@SPID", -1),
                    Fn("@@VERSION", "@@VERSION", "@@VERSION", -1),
                    Fn("DB_NAME", "DB_NAME([database_id])", "DB_NAME()", 8, "database_id (optional)"),
                    Fn("DB_ID", "DB_ID([database_name])", "DB_ID()", 6, "database_name (optional)"),
                    Fn("OBJECT_ID", "OBJECT_ID(object_name [, object_type])", "OBJECT_ID()", 10,
                        "object_name", "object_type (optional)"),
                    Fn("OBJECT_NAME", "OBJECT_NAME(object_id [, database_id])", "OBJECT_NAME()", 12,
                        "object_id", "database_id (optional)"),
                    Fn("OBJECT_SCHEMA_NAME", "OBJECT_SCHEMA_NAME(object_id [, database_id])", "OBJECT_SCHEMA_NAME()", 19,
                        "object_id", "database_id (optional)"),
                    Fn("SCHEMA_NAME", "SCHEMA_NAME([schema_id])", "SCHEMA_NAME()", 12, "schema_id (optional)"),
                    Fn("SCHEMA_ID", "SCHEMA_ID([schema_name])", "SCHEMA_ID()", 10, "schema_name (optional)"),
                    Fn("USER_NAME", "USER_NAME([id])", "USER_NAME()", 10, "id (optional)"),
                    Fn("USER_ID", "USER_ID([user])", "USER_ID()", 8, "user (optional)"),
                    Fn("SUSER_NAME", "SUSER_NAME([server_user_id])", "SUSER_NAME()", 11, "server_user_id (optional)"),
                    Fn("SUSER_SNAME", "SUSER_SNAME([server_user_sid])", "SUSER_SNAME()", 12, "server_user_sid (optional)"),
                    Fn("SUSER_SID", "SUSER_SID([login [, param2]])", "SUSER_SID()", 10, "login (optional)"),
                    Fn("SYSTEM_USER", "SYSTEM_USER", "SYSTEM_USER", -1),
                    Fn("CURRENT_USER", "CURRENT_USER", "CURRENT_USER", -1),
                    Fn("SESSION_USER", "SESSION_USER", "SESSION_USER", -1),
                    Fn("ORIGINAL_LOGIN", "ORIGINAL_LOGIN()", "ORIGINAL_LOGIN()", -1),
                    Fn("HOST_NAME", "HOST_NAME()", "HOST_NAME()", -1),
                    Fn("APP_NAME", "APP_NAME()", "APP_NAME()", -1),
                    Fn("TYPE_NAME", "TYPE_NAME(type_id)", "TYPE_NAME()", 10, "type_id"),
                    Fn("TYPE_ID", "TYPE_ID(type_name)", "TYPE_ID()", 8, "type_name"),
                    Fn("ERROR_NUMBER", "ERROR_NUMBER()", "ERROR_NUMBER()", -1),
                    Fn("ERROR_MESSAGE", "ERROR_MESSAGE()", "ERROR_MESSAGE()", -1),
                    Fn("ERROR_SEVERITY", "ERROR_SEVERITY()", "ERROR_SEVERITY()", -1),
                    Fn("ERROR_STATE", "ERROR_STATE()", "ERROR_STATE()", -1),
                    Fn("ERROR_LINE", "ERROR_LINE()", "ERROR_LINE()", -1),
                    Fn("ERROR_PROCEDURE", "ERROR_PROCEDURE()", "ERROR_PROCEDURE()", -1),
                    Fn("XACT_STATE", "XACT_STATE()", "XACT_STATE()", -1)
                };
            }

            private static BuiltInFunctionInfo Fn(string name, string signature, string insert, int cursorOffset, params string[] parameters)
            {
                return new BuiltInFunctionInfo
                {
                    Name = name,
                    Signature = signature,
                    InsertText = insert,
                    CursorOffset = cursorOffset,
                    Parameters = parameters ?? Array.Empty<string>()
                };
            }

            private static Dictionary<string, BuiltInFunctionInfo> _builtInByName;

            /// <summary>按名查找内建函数（悬停 QuickInfo 用）。</summary>
            internal static bool TryGetBuiltInFunction(string name, out string displayName, out string signature, out string[] parameters)
            {
                displayName = null;
                signature = null;
                parameters = null;
                if (string.IsNullOrEmpty(name)) return false;
                string key = name.Trim('[', ']', '"');
                if (string.IsNullOrEmpty(key)) return false;
                if (_builtInByName == null)
                {
                    var map = new Dictionary<string, BuiltInFunctionInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var fn in BuiltInFunctionInfos)
                    {
                        if (fn != null && !string.IsNullOrEmpty(fn.Name))
                            map[fn.Name] = fn;
                    }
                    _builtInByName = map;
                }
                BuiltInFunctionInfo info;
                if (!_builtInByName.TryGetValue(key, out info) || info == null)
                    return false;
                displayName = info.Name;
                signature = info.Signature;
                parameters = info.Parameters;
                return true;
            }

            #endregion
        }
    }
}

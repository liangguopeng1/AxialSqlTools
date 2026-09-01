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

                    case CompletionContext.FromClause:
                        if (fromName != null && fromName.ExcessiveDots)
                            break;
                        AddFromClauseItems(items, fromName, catalog, settings, connInfo, prefix);
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
                        AddFromClauseItems(items, fromName, catalog, settings, connInfo, prefix);
                        break;

                    case CompletionContext.InsertColumnList:
                        AddColumnsFromFromAliases(items, catalog, local, settings, connInfo);
                        AddAllColumnsCompletions(items, catalog, local, settings, connInfo, withAliasPrefix: false);
                        break;

                    case CompletionContext.UpdateTarget:
                    case CompletionContext.DeleteTarget:
                        AddFromClauseItems(items, fromName, catalog, settings, connInfo, prefix);
                        if (settings.includeKeywords && !string.IsNullOrEmpty(prefix)
                            && (fromName == null || !fromName.AfterDot))
                            AddKeywords(items, new[] { "SET", "FROM", "WHERE" });
                        break;

                    case CompletionContext.SelectElements:
                        if (settings.includeKeywords)
                        {
                            AddKeywords(items, SelectClauseKeywords);
                        }
                        AddColumnsAndFunctions(items, catalog, local, settings, connInfo);
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
                }

                return items;
            }

            private void AddFromClauseItems(
                List<CompletionItem> items,
                FromObjectNameContext fromName,
                MetadataCatalog catalog,
                IntelliSenseSettings settings,
                ScriptFactoryAccess.ConnectionInfo connInfo,
                string namePrefix = null)
            {
                if (fromName == null || !fromName.InFromClause)
                {
                    AddDatabases(items, connInfo, settings);
                    AddLinkedServers(items, connInfo, settings);
                    AddTablesViewsAndRoutines(items, catalog, settings, includeTableFunctions: true, fromName: fromName, connInfo: connInfo, namePrefix: namePrefix);
                    return;
                }
                if (fromName.ExcessiveDots)
                    return;

                int segCount = fromName.Segments?.Count ?? 0;
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
                            AddTablesInSchema(items, remote, null, settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                            return;
                        }
                        if (segCount == 3)
                        {
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
                        AddTablesInSchema(items, remote, null, settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
                        return;
                    }
                }

                bool qualified = segCount > 0 || fromName.AfterDot;
                if (segCount == 0)
                {
                    // 数据库优先加入，避免被表列表占满 maxCompletionItems
                    AddDatabases(items, connInfo, settings);
                    AddLinkedServers(items, connInfo, settings);
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
                    if (segCount == 1 && IsDatabaseName(connInfo, fromName.Segments[0]))
                    {
                        var remote = ResolveDatabaseCatalog(connInfo, catalog, fromName.Segments[0]);
                        AddTablesInSchema(items, remote, null, settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
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

                if (segCount == 1 && IsDatabaseName(connInfo, fromName.Segments[0]))
                {
                    var remote = ResolveDatabaseCatalog(connInfo, catalog, fromName.Segments[0]);
                    AddTablesInSchema(items, remote, null, settings, fromName, connInfo, includeSystemObjects: false, namePrefix: namePrefix);
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

            private void AddSchemasForDatabase(List<CompletionItem> items, ScriptFactoryAccess.ConnectionInfo connInfo, string database)
            {
                var cat = GetCatalogNonBlocking(connInfo, database);
                if (cat == null) return;
                var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in cat.Tables)
                {
                    if (!string.IsNullOrEmpty(t.Schema)) schemas.Add(t.Schema);
                }
                foreach (var v in cat.Views)
                {
                    if (!string.IsNullOrEmpty(v.Schema)) schemas.Add(v.Schema);
                }
                foreach (var s in schemas.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    items.Add(new CompletionItem(s, "[" + s + "]", CompletionKind.Schema, "架构 (" + database + ")"));
                }
            }

            private void AddTablesInSchema(List<CompletionItem> items, MetadataCatalog catalog, string schema, IntelliSenseSettings settings, FromObjectNameContext fromName, ScriptFactoryAccess.ConnectionInfo connInfo, bool includeSystemObjects, string namePrefix = null)
            {
                if (catalog == null) return;
                bool tableNameOnly = fromName != null && fromName.InFromClause && fromName.UsesDoubleDot;
                string filter = GetLastSegment(namePrefix ?? fromName?.Partial);
                int cap = Math.Max(50, settings.maxCompletionItems * 3);
                // 插入文本策略只算一次，避免每张表都 IsDatabaseName/连库
                var insertMode = ResolveTableInsertMode(fromName, connInfo);
                int added = 0;
                foreach (var t in catalog.Tables)
                {
                    if (!string.IsNullOrEmpty(schema) && !string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!ObjectNameMatchesFilter(t.Name, filter))
                        continue;
                    string insert = BuildTableInsertText(fromName, t, connInfo, settings, insertMode);
                    string display = tableNameOnly ? t.Name : t.QualifiedName;
                    items.Add(new CompletionItem(display, insert, CompletionKind.Table, BuildTableDescription(t)));
                    if (++added >= cap) return;
                }
                foreach (var v in catalog.Views)
                {
                    if (!string.IsNullOrEmpty(schema) && !string.Equals(v.Schema, schema, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!ObjectNameMatchesFilter(v.Name, filter))
                        continue;
                    string insert = BuildTableInsertText(fromName, v, connInfo, settings, insertMode);
                    string display = tableNameOnly ? v.Name : v.QualifiedName;
                    items.Add(new CompletionItem(display, insert, CompletionKind.View, BuildTableDescription(v)));
                    if (++added >= cap) return;
                }
                if (includeSystemObjects && settings.includeSystemObjects && string.IsNullOrEmpty(schema)
                    && string.IsNullOrEmpty(filter))
                {
                    AddSystemObjects(items);
                }
            }

            private static bool ObjectNameMatchesFilter(string objectName, string filter)
            {
                if (string.IsNullOrEmpty(filter)) return true;
                if (string.IsNullOrEmpty(objectName)) return false;
                if (objectName.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) return true;
                if (objectName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                string collapsedName = CollapseForMatch(objectName);
                string collapsedFilter = CollapseForMatch(filter);
                return !string.IsNullOrEmpty(collapsedFilter)
                    && collapsedName.IndexOf(collapsedFilter, StringComparison.OrdinalIgnoreCase) >= 0;
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

            private TableInsertMode ResolveTableInsertMode(FromObjectNameContext fromName, ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                if (fromName == null || !fromName.InFromClause)
                    return TableInsertMode.FullQualified;
                int segCount = fromName.Segments?.Count ?? 0;
                bool linkedServerContext = segCount > 0 && connInfo != null && IsLinkedServerName(connInfo, fromName.Segments[0]);
                if (fromName.AfterDot || fromName.PartialStartOffset >= 0)
                {
                    if (linkedServerContext)
                    {
                        if (segCount == 1) return TableInsertMode.TableOnly;
                        if (fromName.UsesDoubleDot || segCount >= 2) return TableInsertMode.TableOnly;
                    }
                    if (fromName.UsesDoubleDot) return TableInsertMode.TableOnly;
                    if (segCount >= 2) return TableInsertMode.TableOnly;
                    if (segCount == 1)
                    {
                        if (IsDatabaseName(connInfo, fromName.Segments[0]))
                            return TableInsertMode.SchemaAndTable;
                        return TableInsertMode.TableOnly;
                    }
                    return TableInsertMode.FullQualified;
                }
                if (segCount == 0) return TableInsertMode.FullQualified;
                string dbOrSchema = fromName.Segments[0];
                bool isDb = IsDatabaseName(connInfo, dbOrSchema);
                if (isDb && fromName.UsesDoubleDot) return TableInsertMode.DbDoubleDotTable;
                if (isDb && segCount >= 2) return TableInsertMode.DbDotSchemaTable;
                if (isDb) return TableInsertMode.DbDotSchemaAndTable;
                if (segCount == 1) return TableInsertMode.SchemaDotTable;
                return TableInsertMode.FullQualified;
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

            private void AddTablesViewsAndRoutines(List<CompletionItem> items, MetadataCatalog catalog, IntelliSenseSettings settings, bool includeTableFunctions, FromObjectNameContext fromName = null, ScriptFactoryAccess.ConnectionInfo connInfo = null, string namePrefix = null)
            {
                AddTablesAndViews(items, catalog, settings, fromName, connInfo, namePrefix);
                if (catalog == null) return;
                string filter = GetLastSegment(namePrefix ?? fromName?.Partial);
                var insertMode = ResolveTableInsertMode(fromName, connInfo);
                foreach (var s in catalog.Synonyms)
                {
                    if (!ObjectNameMatchesFilter(s.Name, filter)) continue;
                    items.Add(new CompletionItem(s.QualifiedName, FormatObjectInsert(s, settings), CompletionKind.Synonym, "同义词"));
                }
                if (includeTableFunctions)
                {
                    foreach (var f in catalog.TableFunctions)
                    {
                        if (!ObjectNameMatchesFilter(f.Name, filter)) continue;
                        string insert = fromName != null ? BuildTableInsertText(fromName, f, connInfo, settings, insertMode) : FormatObjectInsert(f, settings);
                        items.Add(new CompletionItem(f.QualifiedName, insert, CompletionKind.TableFunction,
                            BuildRoutineDescription(f)));
                    }
                }
            }

            private void AddTablesAndViews(List<CompletionItem> items, MetadataCatalog catalog, IntelliSenseSettings settings, FromObjectNameContext fromName = null, ScriptFactoryAccess.ConnectionInfo connInfo = null, string namePrefix = null)
            {
                if (catalog == null) return;
                if (connInfo != null && !string.IsNullOrWhiteSpace(connInfo.Database)
                    && !string.Equals(catalog.Database, connInfo.Database, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                string filter = GetLastSegment(namePrefix ?? fromName?.Partial);
                int cap = Math.Max(50, settings.maxCompletionItems * 3);
                var insertMode = ResolveTableInsertMode(fromName, connInfo);
                int added = 0;
                foreach (var t in catalog.Tables)
                {
                    if (!ObjectNameMatchesFilter(t.Name, filter)) continue;
                    string insert = fromName != null ? BuildTableInsertText(fromName, t, connInfo, settings, insertMode) : FormatObjectInsert(t, settings);
                    items.Add(new CompletionItem(t.QualifiedName, insert, CompletionKind.Table,
                        BuildTableDescription(t)));
                    if (++added >= cap) return;
                }
                foreach (var v in catalog.Views)
                {
                    if (!ObjectNameMatchesFilter(v.Name, filter)) continue;
                    string insert = fromName != null ? BuildTableInsertText(fromName, v, connInfo, settings, insertMode) : FormatObjectInsert(v, settings);
                    items.Add(new CompletionItem(v.QualifiedName, insert, CompletionKind.View,
                        BuildTableDescription(v)));
                    if (++added >= cap) return;
                }
                if (settings.includeSystemObjects && fromName == null && string.IsNullOrEmpty(filter))
                {
                    AddSystemObjects(items);
                }
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
                    foreach (var col in tcol.Columns)
                    {
                        string colInsert = FormatColumnInsert(col.Name, settings);
                        string insert = string.IsNullOrEmpty(alias)
                            ? colInsert
                            : FormatIdentifier(alias, settings) + "." + colInsert;
                        items.Add(CreateColumnItem(col.Name, insert, col, kv.Value, tcol, catalog, connInfo));
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
                    if (tref != null && string.Equals(alias, tref.Name, StringComparison.OrdinalIgnoreCase))
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
                    if (tref != null && string.Equals(alias, tref.Name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (alias.Equals(p, StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (p.Length >= 2 && alias.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            private void AddLocalColumns(List<CompletionItem> items, LocalSymbols local, IntelliSenseSettings settings)
            {
                foreach (var cte in local.Ctes)
                {
                    foreach (var col in cte.ColumnNames)
                    {
                        items.Add(new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "CTE 列: " + cte.Name));
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
                            items.Add(new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "临时表列: " + tt.Name));
                        }
                    }
                }
                if (settings.includeLocalVariables)
                {
                    foreach (var tv in local.TableVariables)
                    {
                        foreach (var col in tv.ColumnNames)
                        {
                            items.Add(new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "表变量列: " + tv.Name));
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

                AddProceduresFromCatalog(items, target, settings, schema, nameOnlyInsert, insertSchemaWithName);
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
                if (catalog == null) return;
                foreach (var f in catalog.ScalarFunctions)
                {
                    items.Add(new CompletionItem(f.Name, FormatObjectInsert(f, settings), CompletionKind.ScalarFunction,
                        BuildRoutineDescription(f)));
                }
            }

            private void AddMemberColumns(List<CompletionItem> items, string prefix, MetadataCatalog catalog, LocalSymbols local, IntelliSenseSettings settings, ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                // prefix 形如 "alias." 或 "alias.col"，取 "." 前的标识符
                string owner = GetOwnerBeforeDot(prefix);
                if (string.IsNullOrEmpty(owner)) return;

                // 本地对象优先
                if (AddLocalMemberColumns(items, owner, local, settings))
                {
                    return;
                }

                // 别名映射
                TableRef tref;
                if (local.Aliases.TryGetValue(owner, out tref))
                {
                    var tcol = ResolveTableRef(connInfo, catalog, tref);
                    if (tcol != null)
                    {
                        foreach (var col in tcol.Columns)
                        {
                            items.Add(CreateColumnItem(col.Name, FormatColumnInsert(col.Name, settings), col, tref, tcol, catalog, connInfo));
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
                    foreach (var col in cte.ColumnNames)
                    {
                        items.Add(new CompletionItem(col, FormatColumnInsert(col, settings), CompletionKind.Column, "CTE 列: " + cte.Name));
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

            private void AddSystemObjects(List<CompletionItem> items)
            {
                foreach (var s in MetadataCatalogService.CommonSystemObjects)
                {
                    items.Add(new CompletionItem(s, s, CompletionKind.Table, "系统对象"));
                }
            }

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
                    if (string.IsNullOrEmpty(filterPrefix) || ItemMatchesPrefix(item, filterPrefix))
                        filtered.Add(item);
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
                    filtered = filtered.Take(settings.maxCompletionItems).ToList();
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
                    case CompletionKind.Keyword: return 0;
                    case CompletionKind.ScalarFunction: return 1; // 有前缀时靠 GetMatchScore；空前缀紧随关键字，避免被表挤掉
                    case CompletionKind.Snippet: return 2;
                    case CompletionKind.Table: return 3;
                    case CompletionKind.View: return 4;
                    case CompletionKind.Synonym: return 5;
                    case CompletionKind.TableFunction: return 6;
                    case CompletionKind.Schema: return 7;
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
                string name = GetMatchableName(displayText);
                return name.StartsWith("INFORMATION_SCHEMA.", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("sys.", StringComparison.OrdinalIgnoreCase);
            }

            private static int GetMatchScore(CompletionItem item, string filterPrefix)
            {
                if (string.IsNullOrEmpty(filterPrefix)) return 0;
                string displayText = item?.DisplayText ?? string.Empty;
                if (IsNumericOrIpPrefix(filterPrefix))
                {
                    if (displayText.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase)) return 100;
                    return 0;
                }
                // 全字段：空前缀已在外层保留；有前缀时任一列名前缀命中则保留（略低于精确列）
                if (item != null && item.Kind == CompletionKind.AllColumns)
                {
                    if (displayText.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                        return 90;
                    string insert = item.InsertText ?? string.Empty;
                    foreach (var part in insert.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string col = UnbracketIdentifier(GetLastSegment(part.Trim()));
                        if (string.IsNullOrEmpty(col)) continue;
                        if (col.Equals(filterPrefix, StringComparison.OrdinalIgnoreCase)) return 95;
                        if (col.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase)) return 90;
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
                    if (name.Equals(filterPrefix, StringComparison.OrdinalIgnoreCase)) return 130;
                    if (name.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase)) return 120;
                }

                if (name.Equals(filterPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return item != null && item.Kind == CompletionKind.Snippet ? 110 : 100;
                }
                if (name.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // 子句关键字略高于 CASE 片段：wh → WHERE 优先于 WHEN
                    if (item != null && item.Kind == CompletionKind.Keyword && IsClauseTrailingKeyword(name))
                        return 105;
                    return 100;
                }
                // 多词关键字：in → INNER JOIN、group → GROUP BY
                if (item != null && item.Kind == CompletionKind.Keyword && name.IndexOf(' ') >= 0)
                {
                    int sp = name.IndexOf(' ');
                    string first = sp > 0 ? name.Substring(0, sp) : name;
                    if (first.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                        return IsClauseTrailingKeyword(name) ? 100 : 95;
                }
                // 片段：ss → sess（包含）；仍低于前缀命中，ssf 排在 sess 前
                if (item != null && item.Kind == CompletionKind.Snippet)
                {
                    if (name.IndexOf(filterPrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                        return 80;
                    return 0;
                }
                // 关键字/内建函数只做前缀匹配，避免 pr 命中 DATEPART 等缩写误匹配
                if (item != null && (item.Kind == CompletionKind.Keyword
                    || item.Kind == CompletionKind.ScalarFunction))
                    return 0;

                // 下划线段前缀：rt_fenjian ← fe（≥2 即可；单字母仍不做，避免 a 扫到过多段）
                if (filterPrefix.Length >= 2)
                {
                    var segments = name.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var seg in segments)
                    {
                        if (seg.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase)) return 60;
                    }
                }

                // 短前缀不做缩写/包含匹配，避免 aa 命中 CreateDate、a 命中大量列；
                // 数据库通常很少（十几个），不设此限制。
                if (filterPrefix.Length <= 2
                    && (item == null || item.Kind != CompletionKind.Database))
                    return 0;

                if (name.IndexOf(filterPrefix, StringComparison.OrdinalIgnoreCase) >= 0) return 80;

                string collapsedName = CollapseForMatch(name);
                string collapsedFilter = CollapseForMatch(filterPrefix);
                if (!string.IsNullOrEmpty(collapsedFilter))
                {
                    if (collapsedName.StartsWith(collapsedFilter, StringComparison.OrdinalIgnoreCase)) return 85;
                    if (collapsedName.IndexOf(collapsedFilter, StringComparison.OrdinalIgnoreCase) >= 0) return 65;
                    if (MatchesAbbreviation(collapsedName, collapsedFilter)) return 50;
                }

                var segs = name.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var seg in segs)
                {
                    if (seg.IndexOf(filterPrefix, StringComparison.OrdinalIgnoreCase) >= 0) return 40;
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

            /// <summary>缩写匹配：filter 字符按序出现在 name 中（如 tpr → t_products）。</summary>
            private static bool MatchesAbbreviation(string name, string abbr)
            {
                if (string.IsNullOrEmpty(abbr)) return true;
                if (string.IsNullOrEmpty(name)) return false;
                int j = 0;
                for (int i = 0; i < name.Length && j < abbr.Length; i++)
                {
                    if (char.ToLowerInvariant(name[i]) == char.ToLowerInvariant(abbr[j]))
                    {
                        j++;
                    }
                }
                return j == abbr.Length;
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
                ScriptFactoryAccess.ConnectionInfo connInfo)
            {
                var item = new CompletionItem(display, insert, CompletionKind.Column, BuildColumnDescription(col));
                item.SourceDatabase = ResolveSourceDatabase(tref, catalog, connInfo);
                item.SourceTable = FormatSourceTable(table, tref);
                return item;
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
                "CASE", "WHEN", "THEN", "ELSE", "END"
            };

            /// <summary>GROUP/ORDER 后补 BY。</summary>
            public static readonly string[] GroupOrderByKeywords = { "BY" };

            /// <summary>INSERT 后常见关键字。</summary>
            public static readonly string[] InsertKeywords = { "INTO", "SELECT", "VALUES", "DEFAULT" };

            public static readonly string[] TopLevelKeywords =
            {
                "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "DROP",
                "EXEC", "USE", "DECLARE", "SET", "IF", "BEGIN", "END", "TRUNCATE", "MERGE", "GO"
            };

            /// <summary>FROM 表之后常见子句/连接关键字。</summary>
            public static readonly string[] AfterFromKeywords =
            {
                "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "JOIN",
                "LEFT OUTER JOIN", "RIGHT OUTER JOIN", "FULL OUTER JOIN",
                "CROSS APPLY", "OUTER APPLY",
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
                "HAVING", "ORDER BY",
                "UNION", "UNION ALL", "EXCEPT", "INTERSECT"
            };

            public static readonly string[] CreateObjectKeywords =
            {
                "TABLE", "VIEW", "PROCEDURE", "FUNCTION", "INDEX", "SCHEMA", "TYPE", "TRIGGER"
            };

            public static readonly string[] AlterObjectKeywords =
            {
                "TABLE", "VIEW", "PROCEDURE", "FUNCTION", "INDEX", "SCHEMA", "TRIGGER"
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

            #endregion
        }
    }
}

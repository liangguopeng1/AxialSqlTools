using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// 悬停信息：定位 token、解析 FROM 上下文、匹配元数据，返回结构化 ToolTip 内容。
    /// </summary>
    public class QuickInfoProvider
    {
        private readonly TSql170Parser _parser = new TSql170Parser(true);

        public QuickInfoData GetQuickInfo(
            string fullText,
            int caretOffset,
            MetadataCatalog catalog,
            ScriptFactoryAccess.ConnectionInfo connInfo)
        {
            if (string.IsNullOrEmpty(fullText) || caretOffset < 0)
                return null;

            try
            {
                TSqlScript script;
                using (var reader = new StringReader(fullText))
                {
                    script = _parser.Parse(reader, out var errors) as TSqlScript;
                }
                if (script == null) return null;

                var tokens = script.ScriptTokenStream?.ToList();
                if (tokens == null || tokens.Count == 0) return null;

                var hover = QuickInfoSqlContext.TryGetHoverToken(tokens, caretOffset);
                if (hover == null || string.IsNullOrEmpty(hover.Name)) return null;

                string dataSource = FormatDataSource(connInfo);
                string defaultDb = connInfo?.Database ?? catalog?.Database;

                // EXEC 存储过程悬停（含跨库 caiwu..proc）
                var procInfo = TryBuildProcedureQuickInfo(fullText, caretOffset, hover.Name, catalog, connInfo, dataSource, defaultDb);
                if (procInfo != null)
                    return procInfo;

                // 优先按悬停词命中 FROM/JOIN 中的表（支持 db.schema.table / db..table / JOIN 表）
                var hoverTableRef = QuickInfoSqlContext.TryResolveTableByHoverName(tokens, caretOffset, hover.Name);
                if (hoverTableRef != null && !hover.HasOwner)
                {
                    var ht = ResolveTable(connInfo, catalog, hoverTableRef);
                    if (ht != null)
                        return BuildTableQuickInfo(dataSource, defaultDb, hoverTableRef, ht);
                }
                if (hoverTableRef != null && hover.HasOwner
                    && string.Equals(hoverTableRef.Name, hover.Name, StringComparison.OrdinalIgnoreCase))
                {
                    var schemaQualified = new TableRef
                    {
                        Database = hoverTableRef.Database,
                        Schema = string.IsNullOrEmpty(hoverTableRef.Schema) ? hover.Owner : hoverTableRef.Schema,
                        Name = hover.Name
                    };
                    var ht = ResolveTable(connInfo, catalog, schemaQualified);
                    if (ht != null)
                        return BuildTableQuickInfo(dataSource, defaultDb, schemaQualified, ht);
                }

                var fromRef = hoverTableRef ?? QuickInfoSqlContext.TryResolveFromTable(tokens, caretOffset);

                if (hover.HasOwner)
                {
                    var aliasRef = QuickInfoSqlContext.TryResolveAlias(tokens, caretOffset, hover.Owner)
                                   ?? ResolveAliasOwner(hover.Owner, tokens, caretOffset, fromRef);
                    if (aliasRef != null)
                    {
                        var table = ResolveTable(connInfo, catalog, aliasRef);
                        if (table != null)
                        {
                            var col = FindColumn(table, hover.Name);
                            if (col != null)
                                return BuildColumnQuickInfo(dataSource, defaultDb, aliasRef, table, col);
                        }
                    }
                    // dbo.TableName / schema.TableName
                    var schemaTable = ResolveTable(connInfo, catalog, new TableRef
                    {
                        Schema = hover.Owner,
                        Name = hover.Name,
                        Database = fromRef?.Database ?? hoverTableRef?.Database
                    });
                    if (schemaTable != null)
                        return BuildTableQuickInfo(dataSource, defaultDb,
                            new TableRef
                            {
                                Database = fromRef?.Database ?? hoverTableRef?.Database,
                                Schema = schemaTable.Schema,
                                Name = schemaTable.Name
                            }, schemaTable);
                }

                var directTable = ResolveTable(connInfo, catalog, new TableRef
                {
                    Name = hover.Name,
                    Schema = fromRef?.Schema,
                    Database = fromRef?.Database
                });
                if (directTable != null && string.Equals(directTable.Name, hover.Name, StringComparison.OrdinalIgnoreCase))
                    return BuildTableQuickInfo(dataSource, defaultDb, ResolveTableRef(fromRef, directTable), directTable);

                TableColumnInfo contextTable = null;
                TableRef contextRef = null;
                if (fromRef != null)
                {
                    contextTable = ResolveTable(connInfo, catalog, fromRef);
                    contextRef = fromRef;
                }
                if (contextTable != null)
                {
                    var col = FindColumn(contextTable, hover.Name);
                    if (col != null)
                        return BuildColumnQuickInfo(dataSource, defaultDb, contextRef, contextTable, col);
                }

                if (catalog != null)
                {
                    var tcol = catalog.FindTableOrView(null, hover.Name);
                    if (tcol != null)
                        return BuildTableQuickInfo(dataSource, catalog.Database, new TableRef
                        {
                            Database = catalog.Database,
                            Schema = tcol.Schema,
                            Name = tcol.Name
                        }, tcol);

                    foreach (var t in catalog.Tables.Concat(catalog.Views))
                    {
                        var col = FindColumn(t, hover.Name);
                        if (col != null)
                            return BuildColumnQuickInfo(dataSource, defaultDb, new TableRef
                            {
                                Database = catalog.Database,
                                Schema = t.Schema,
                                Name = t.Name
                            }, t, col);
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static readonly Regex FromClauseRegex = new Regex(
            @"\bFROM\s+(?:\[?(?<db>[^\s\.\[\]]+)\]?\s*\.\s*(?:\.(?:\[?(?<schema>[^\s\.\[\]]+)\]?\s*\.)?)?|\[?(?<schema2>[^\s\.\[\]]+)\]?\s*\.\s*)?\[?(?<table>[^\s\.\[\],]+)\]?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>ScriptDOM 未命中时，用行内词 + FROM/元数据目录兜底。</summary>
        public QuickInfoData GetQuickInfoByWord(
            string fullText,
            int caretOffset,
            string hoverWord,
            MetadataCatalog catalog,
            ScriptFactoryAccess.ConnectionInfo connInfo)
        {
            if (string.IsNullOrEmpty(hoverWord)) return null;
            try
            {
                string cleanWord = hoverWord.Trim('[', ']', '"');
                if (string.IsNullOrEmpty(cleanWord)) return null;
                string dataSource = FormatDataSource(connInfo);
                string defaultDb = connInfo?.Database ?? catalog?.Database;

                var procInfo = TryBuildProcedureQuickInfo(fullText, caretOffset, cleanWord, catalog, connInfo, dataSource, defaultDb);
                if (procInfo != null)
                    return procInfo;

                TableRef fromRef = null;
                if (!string.IsNullOrEmpty(fullText))
                {
                    try
                    {
                        using (var reader = new StringReader(fullText))
                        {
                            var script = _parser.Parse(reader, out var errors) as TSqlScript;
                            var tokens = script?.ScriptTokenStream?.ToList();
                            if (tokens != null)
                                fromRef = QuickInfoSqlContext.TryResolveTableByHoverName(tokens, caretOffset, cleanWord)
                                          ?? QuickInfoSqlContext.TryResolveFromTable(tokens, caretOffset);
                        }
                    }
                    catch
                    {
                    }
                    if (fromRef == null)
                        fromRef = TryParseFromClause(fullText);
                }

                // 悬停词就是 FROM/JOIN 中的表名时，直接用完整三段引用（含跨库）
                if (fromRef != null && string.Equals(fromRef.Name, cleanWord, StringComparison.OrdinalIgnoreCase))
                {
                    var direct = ResolveTable(connInfo, catalog, fromRef);
                    if (direct != null)
                        return BuildTableQuickInfo(dataSource, defaultDb, fromRef, direct);
                }

                var asTable = ResolveTable(connInfo, catalog, new TableRef
                {
                    Name = cleanWord,
                    Schema = fromRef?.Schema,
                    Database = fromRef?.Database
                });
                if (asTable != null && string.Equals(asTable.Name, cleanWord, StringComparison.OrdinalIgnoreCase))
                    return BuildTableQuickInfo(dataSource, defaultDb, ResolveTableRef(fromRef, asTable), asTable);

                if (catalog != null)
                {
                    var named = catalog.FindTableOrView(null, cleanWord);
                    if (named != null)
                        return BuildTableQuickInfo(dataSource, catalog.Database, new TableRef
                        {
                            Database = catalog.Database,
                            Schema = named.Schema,
                            Name = named.Name
                        }, named);
                }

                if (fromRef != null)
                {
                    var contextTable = ResolveTable(connInfo, catalog, fromRef);
                    if (contextTable != null)
                    {
                        var col = FindColumn(contextTable, cleanWord);
                        if (col != null)
                            return BuildColumnQuickInfo(dataSource, defaultDb, fromRef, contextTable, col);
                    }
                }

                if (catalog != null)
                {
                    foreach (var t in catalog.Tables.Concat(catalog.Views))
                    {
                        var col = FindColumn(t, cleanWord);
                        if (col != null)
                            return BuildColumnQuickInfo(dataSource, defaultDb, new TableRef
                            {
                                Database = catalog.Database,
                                Schema = t.Schema,
                                Name = t.Name
                            }, t, col);
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        private QuickInfoData TryBuildProcedureQuickInfo(
            string fullText,
            int caretOffset,
            string hoverName,
            MetadataCatalog catalog,
            ScriptFactoryAccess.ConnectionInfo connInfo,
            string dataSource,
            string defaultDb)
        {
            var rref = QuickInfoSqlContext.TryResolveExecRoutine(fullText, caretOffset, hoverName)
                       ?? QuickInfoSqlContext.TryResolveQualifiedRoutine(fullText, caretOffset, hoverName);
            if (rref == null || string.IsNullOrEmpty(rref.Name))
                return null;

            // EXEC db.proc：若首段是库名则当跨库，否则当架构
            if (string.IsNullOrEmpty(rref.Database) && !string.IsNullOrEmpty(rref.Schema)
                && connInfo != null && MetadataCatalogService.Instance.ContainsDatabase(connInfo, rref.Schema))
            {
                rref.Database = rref.Schema;
                rref.Schema = "dbo";
            }

            var routine = ResolveProcedure(connInfo, catalog, rref);
            if (routine == null)
                return null;
            // 悬停展示完整过程 SQL（按需单条拉取并缓存到 RoutineInfo.Definition）
            if (connInfo != null)
            {
                MetadataCatalogService.Instance.EnsureRoutineDefinition(
                    connInfo, rref.Database ?? defaultDb, routine);
            }
            return BuildProcedureQuickInfo(dataSource, defaultDb, rref, routine);
        }

        private RoutineInfo ResolveProcedure(
            ScriptFactoryAccess.ConnectionInfo connInfo,
            MetadataCatalog catalog,
            QuickInfoSqlContext.RoutineRef rref)
        {
            if (rref == null || string.IsNullOrEmpty(rref.Name)) return null;
            MetadataCatalog target = catalog;
            if (connInfo != null && !string.IsNullOrWhiteSpace(rref.Database))
            {
                if (catalog == null || !string.Equals(catalog.Database, rref.Database, StringComparison.OrdinalIgnoreCase))
                {
                    target = MetadataCatalogService.Instance.GetCachedCatalog(connInfo, rref.Database);
                    if (target == null || !target.RoutinesLoaded)
                    {
                        // 跨库过程：同步补过程目录（有表缓存时只补过程，不整库重建）
                        target = MetadataCatalogService.Instance.GetOrBuildCatalog(connInfo, rref.Database, requireRoutines: true)
                                 ?? target;
                    }
                }
            }
            else if (target != null && !target.RoutinesLoaded && connInfo != null)
            {
                target = MetadataCatalogService.Instance.GetOrBuildCatalog(connInfo, null, requireRoutines: true)
                         ?? target;
            }

            if (target == null) return null;
            string schema = string.IsNullOrEmpty(rref.Schema) ? null : rref.Schema;
            return target.FindProcedure(schema, rref.Name)
                   ?? target.FindProcedure(null, rref.Name);
        }

        private QuickInfoData BuildProcedureQuickInfo(
            string dataSource,
            string defaultDb,
            QuickInfoSqlContext.RoutineRef rref,
            RoutineInfo routine)
        {
            var data = new QuickInfoData();
            string db = rref?.Database ?? defaultDb;
            AddHeaderLine(data, "数据源", dataSource);
            AddHeaderLine(data, "数据库", db);
            AddHeaderLine(data, "架构", routine?.Schema ?? rref?.Schema ?? "dbo");
            AddHeaderLine(data, "存储过程", routine?.Name ?? rref?.Name);
            if (routine?.Parameters != null)
                AddHeaderLine(data, "参数数", routine.Parameters.Count.ToString());
            if (!string.IsNullOrEmpty(routine?.Description))
                data.Description = routine.Description;
            data.DdlText = QuickInfoDdlBuilder.BuildProcedureSignature(routine);
            data.CanGoToSource = !string.IsNullOrEmpty(data.DdlText);
            data.SourceDatabase = db;
            data.SourceSchema = routine?.Schema ?? rref?.Schema ?? "dbo";
            data.SourceObjectName = routine?.Name ?? rref?.Name;
            return data.IsEmpty ? null : data;
        }

        private static TableRef TryParseFromClause(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return null;
            var m = FromClauseRegex.Match(sql);
            if (!m.Success) return null;
            string table = m.Groups["table"].Value;
            if (string.IsNullOrEmpty(table)) return null;
            string db = m.Groups["db"].Success ? m.Groups["db"].Value : null;
            string schema = m.Groups["schema"].Success ? m.Groups["schema"].Value
                : (m.Groups["schema2"].Success ? m.Groups["schema2"].Value : null);
            var tref = new TableRef
            {
                Database = db,
                Schema = schema,
                Name = table
            };
            if (!string.IsNullOrEmpty(tref.Database) && string.IsNullOrEmpty(tref.Schema))
                tref.Schema = "dbo";
            return tref;
        }

        private static TableRef ResolveAliasOwner(
            string owner,
            List<TSqlParserToken> tokens,
            int offset,
            TableRef fromRef)
        {
            if (fromRef == null || string.IsNullOrEmpty(owner)) return null;
            int fromIdx = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t != null && t.Offset <= offset
                    && string.Equals(t.Text, "FROM", StringComparison.OrdinalIgnoreCase))
                    fromIdx = i;
            }
            if (fromIdx < 0) return fromRef;
            int scanEnd = int.MaxValue;
            for (int i = fromIdx + 1; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == null) continue;
                string kw = t.Text?.ToUpperInvariant();
                if (kw == "WHERE" || kw == "JOIN" || kw == "GROUP" || kw == "ORDER") { scanEnd = t.Offset; break; }
            }
            var names = new List<string>();
            for (int i = fromIdx + 1; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == null || t.Offset >= scanEnd) break;
                if (t.TokenType == TSqlTokenType.Dot) continue;
                if (t.TokenType == TSqlTokenType.Identifier || t.TokenType == TSqlTokenType.QuotedIdentifier
                    || t.TokenType == TSqlTokenType.Integer)
                    names.Add(t.Text.Trim('[', ']', '"'));
            }
            if (names.Count >= 2 && string.Equals(names[names.Count - 1], owner, StringComparison.OrdinalIgnoreCase))
            {
                string tableName = names[names.Count - 2];
                if (names.Count == 2 && fromRef != null && string.Equals(fromRef.Name, tableName, StringComparison.OrdinalIgnoreCase))
                    return fromRef;
                if (fromRef != null && string.Equals(fromRef.Name, tableName, StringComparison.OrdinalIgnoreCase))
                    return fromRef;
                return new TableRef
                {
                    Database = fromRef?.Database,
                    Schema = fromRef?.Schema,
                    Name = tableName
                };
            }
            if (string.Equals(fromRef.Name, owner, StringComparison.OrdinalIgnoreCase))
                return fromRef;
            return null;
        }

        private static TableRef ResolveTableRef(TableRef fromRef, TableColumnInfo table)
        {
            if (table == null) return fromRef;
            return new TableRef
            {
                LinkedServer = fromRef?.LinkedServer,
                Database = fromRef?.Database,
                Schema = table.Schema,
                Name = table.Name
            };
        }

        private TableColumnInfo ResolveTable(
            ScriptFactoryAccess.ConnectionInfo connInfo,
            MetadataCatalog catalog,
            TableRef tref)
        {
            if (tref == null || string.IsNullOrEmpty(tref.Name)) return null;
            MetadataCatalog target = catalog;
            if (connInfo != null && !string.IsNullOrWhiteSpace(tref.LinkedServer)
                && !string.IsNullOrWhiteSpace(tref.Database))
            {
                target = MetadataCatalogService.Instance.GetOrBuildLinkedCatalog(
                    connInfo, tref.LinkedServer, tref.Database);
            }
            else if (connInfo != null && !string.IsNullOrWhiteSpace(tref.Database))
            {
                if (catalog == null || !string.Equals(catalog.Database, tref.Database, StringComparison.OrdinalIgnoreCase))
                {
                    target = MetadataCatalogService.Instance.GetCachedCatalog(connInfo, tref.Database);
                    if (target == null)
                    {
                        MetadataCatalogService.Instance.EnsureCatalogBuilding(connInfo, tref.Database);
                        return null;
                    }
                }
            }
            if (target != null)
            {
                var found = target.FindTableOrView(tref.Schema, tref.Name);
                if (found != null) return found;
            }
            return null;
        }

        private static ColumnInfo FindColumn(TableColumnInfo table, string name)
        {
            if (table?.Columns == null || string.IsNullOrEmpty(name)) return null;
            return table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string FormatDataSource(ScriptFactoryAccess.ConnectionInfo connInfo)
        {
            if (connInfo == null || string.IsNullOrWhiteSpace(connInfo.ServerName))
                return string.Empty;
            string server = connInfo.ServerName.Trim();
            if (!server.StartsWith("@", StringComparison.Ordinal) && !server.Contains("\\"))
                return "@" + server;
            return server;
        }

        private QuickInfoData BuildTableQuickInfo(string dataSource, string defaultDb, TableRef tref, TableColumnInfo table)
        {
            var data = new QuickInfoData();
            string db = tref?.Database ?? defaultDb;
            string ds = dataSource;
            if (!string.IsNullOrEmpty(tref?.LinkedServer))
            {
                string ls = tref.LinkedServer.Trim();
                ds = ls.StartsWith("@", StringComparison.Ordinal) || ls.Contains("\\") ? ls : "@" + ls;
            }
            AddHeaderLine(data, "数据源", ds);
            if (!string.IsNullOrEmpty(tref?.LinkedServer))
                AddHeaderLine(data, "链接服务器", tref.LinkedServer);
            AddHeaderLine(data, "数据库", db);
            AddHeaderLine(data, "架构", table?.Schema ?? tref?.Schema ?? "dbo");
            AddHeaderLine(data, table != null && table.IsView ? "视图" : "表", table?.Name ?? tref?.Name);
            if (!string.IsNullOrEmpty(table?.Description))
                data.Description = table.Description;
            data.DdlText = QuickInfoDdlBuilder.BuildCreateTable(table);
            return data.IsEmpty ? null : data;
        }

        private QuickInfoData BuildColumnQuickInfo(
            string dataSource,
            string defaultDb,
            TableRef tref,
            TableColumnInfo table,
            ColumnInfo col)
        {
            var data = new QuickInfoData();
            string db = tref?.Database ?? defaultDb;
            string ds = dataSource;
            if (!string.IsNullOrEmpty(tref?.LinkedServer))
            {
                string ls = tref.LinkedServer.Trim();
                ds = ls.StartsWith("@", StringComparison.Ordinal) || ls.Contains("\\") ? ls : "@" + ls;
            }
            AddHeaderLine(data, "数据源", ds);
            if (!string.IsNullOrEmpty(tref?.LinkedServer))
                AddHeaderLine(data, "链接服务器", tref.LinkedServer);
            AddHeaderLine(data, "数据库", db);
            AddHeaderLine(data, "架构", table?.Schema ?? tref?.Schema ?? "dbo");
            AddHeaderLine(data, table != null && table.IsView ? "视图" : "表", table?.Name ?? tref?.Name);
            AddHeaderLine(data, "列", col?.Name);
            if (!string.IsNullOrEmpty(col?.Description))
                data.Description = col.Description;
            data.DdlText = QuickInfoDdlBuilder.BuildColumnDdl(table, col);
            return data.IsEmpty ? null : data;
        }

        private static void AddHeaderLine(QuickInfoData data, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            data.HeaderLines.Add(label + ": " + value);
        }
    }
}

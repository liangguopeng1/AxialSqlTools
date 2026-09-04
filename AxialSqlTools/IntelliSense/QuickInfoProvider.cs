using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// 悬停信息：定位 token、解析 FROM 上下文、匹配元数据，返回结构化 ToolTip 内容。
    /// </summary>
    public class QuickInfoProvider
    {
        private ScriptFactoryAccess.ConnectionInfo _hoverConn;

        public QuickInfoData GetQuickInfo(
            string fullText,
            int caretOffset,
            MetadataCatalog catalog,
            ScriptFactoryAccess.ConnectionInfo connInfo)
        {
            if (string.IsNullOrEmpty(fullText) || caretOffset < 0)
                return null;

            _hoverConn = connInfo;
            try
            {
                if (CompletionEngine.ScanPrefixState(fullText, caretOffset, out string useDb))
                    return null;
                if (!string.IsNullOrEmpty(useDb) && connInfo != null &&
                    (catalog == null || !string.Equals(catalog.Database, useDb, StringComparison.OrdinalIgnoreCase)))
                {
                    var useCat = MetadataCatalogService.Instance.GetCachedCatalog(connInfo, useDb);
                    if (useCat != null)
                        catalog = useCat;
                }
                if (!CompletionEngine.TryGetParseSlice(fullText, caretOffset, out string slice, out int sliceStart))
                    return null;
                int localOffset = caretOffset - sliceStart;
                if (localOffset < 0) localOffset = 0;
                CompletionEngine.GetTokensForSlice(slice, out var tokens);
                if (tokens == null || tokens.Count == 0) return null;

                var hover = QuickInfoSqlContext.TryGetHoverToken(tokens, localOffset);
                if (hover == null || string.IsNullOrEmpty(hover.Name)) return null;

                string dataSource = FormatDataSource(connInfo);
                string defaultDb = !string.IsNullOrEmpty(useDb) ? useDb : (connInfo?.Database ?? catalog?.Database);

                var locals = CompletionEngine.CollectQueryLocalsFromTokens(tokens, localOffset, slice, fullText, caretOffset);
                if (string.Equals(hover.Name, "*", StringComparison.Ordinal))
                    return TryBuildSelectStarQuickInfo(locals, hover.HasOwner ? hover.Owner : null, catalog, connInfo, dataSource, defaultDb);

                // COUNT( / ISNULL( 等：后接 '(' 时优先当内建函数，避免同名表抢走
                if (!hover.HasOwner)
                {
                    var fnCall = TryBuildBuiltInFunctionQuickInfo(hover.Name, tokens, localOffset, requireParen: true);
                    if (fnCall != null)
                        return fnCall;
                }

                if (!hover.HasOwner)
                {
                    var derived = FindDerived(locals, hover.Name);
                    if (derived != null)
                        return BuildDerivedTableQuickInfo(derived);
                    var aliasTable = TryBuildAliasTableQuickInfo(
                        locals, hover.Name, catalog, connInfo, dataSource, defaultDb);
                    if (aliasTable != null)
                        return aliasTable;
                }
                else
                {
                    var dcol = TryBuildDerivedColumnQuickInfo(locals, hover.Owner, hover.Name);
                    if (dcol != null)
                        return dcol;
                    var aliasCol = TryBuildAliasColumnQuickInfo(
                        locals, hover.Owner, hover.Name, catalog, connInfo, dataSource, defaultDb);
                    if (aliasCol != null)
                        return aliasCol;
                }

                // EXEC 存储过程悬停（含跨库 caiwu..proc）
                var procInfo = TryBuildProcedureQuickInfo(fullText, caretOffset, hover.Name, catalog, connInfo, dataSource, defaultDb);
                if (procInfo != null)
                    return procInfo;

                // 优先按悬停词命中 FROM/JOIN 中的表（支持 server.db.schema.table / db..table）
                var hoverTableRef = QuickInfoSqlContext.TryResolveTableByHoverName(tokens, localOffset, hover.Name);
                if (hoverTableRef != null
                    && (!hover.HasOwner
                        || string.Equals(hoverTableRef.Name, hover.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var ht = ResolveTable(connInfo, catalog, hoverTableRef);
                    if (ht != null)
                        return BuildTableQuickInfo(dataSource, defaultDb, hoverTableRef, ht);
                }

                var fromRef = hoverTableRef ?? QuickInfoSqlContext.TryResolveFromTable(tokens, localOffset);

                if (hover.HasOwner)
                {
                    var aliasRef = QuickInfoSqlContext.TryResolveAlias(tokens, localOffset, hover.Owner)
                                   ?? ResolveAliasOwner(hover.Owner, tokens, localOffset, fromRef)
                                   ?? TryResolveAliasFromLocals(locals, hover.Owner);
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
                    // dbo.TableName / schema.TableName（四段名须带 LinkedServer）
                    var schemaTable = ResolveTable(connInfo, catalog, new TableRef
                    {
                        Schema = hover.Owner,
                        Name = hover.Name,
                        Database = fromRef?.Database ?? hoverTableRef?.Database,
                        LinkedServer = fromRef?.LinkedServer ?? hoverTableRef?.LinkedServer
                    });
                    if (schemaTable != null)
                        return BuildTableQuickInfo(dataSource, defaultDb,
                            new TableRef
                            {
                                LinkedServer = fromRef?.LinkedServer ?? hoverTableRef?.LinkedServer,
                                Database = fromRef?.Database ?? hoverTableRef?.Database,
                                Schema = schemaTable.Schema,
                                Name = schemaTable.Name
                            }, schemaTable);
                }

                // alias.col / schema.col：列名不能再当表名去当前库匹配
                // 未限定表名不得继承其它 FROM 表的库名（避免 BK_KuFang 误用子查询里 kucun_zong 的 rt_storage）
                if (!hover.HasOwner)
                {
                    bool sameTable = fromRef != null
                        && string.Equals(fromRef.Name, hover.Name, StringComparison.OrdinalIgnoreCase);
                    var directTable = ResolveTable(connInfo, catalog, new TableRef
                    {
                        Name = hover.Name,
                        Schema = sameTable ? fromRef.Schema : null,
                        Database = sameTable ? fromRef.Database : null,
                        LinkedServer = sameTable ? fromRef.LinkedServer : null
                    });
                    if (directTable != null && string.Equals(directTable.Name, hover.Name, StringComparison.OrdinalIgnoreCase))
                        return BuildTableQuickInfo(dataSource, defaultDb, ResolveTableRef(fromRef, directTable), directTable);
                }

                if (!hover.HasOwner)
                {
                    var fromCol = TryBuildUnqualifiedColumnQuickInfo(
                        locals, fromRef, hover.Name, catalog, connInfo, dataSource, defaultDb);
                    if (fromCol != null)
                        return fromCol;
                }

                if (catalog != null && !hover.HasOwner)
                {
                    var tcol = catalog.FindTableOrView(null, hover.Name);
                    if (tcol != null)
                        return BuildTableQuickInfo(dataSource, catalog.Database, new TableRef
                        {
                            Database = catalog.Database,
                            Schema = tcol.Schema,
                            Name = tcol.Name
                        }, tcol);
                }

                if (!hover.HasOwner)
                {
                    var dcol = TryBuildDerivedColumnQuickInfo(locals, null, hover.Name);
                    if (dcol != null)
                        return dcol;
                    return TryBuildBuiltInFunctionQuickInfo(hover.Name, tokens, localOffset, requireParen: false);
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
            _hoverConn = connInfo;
            try
            {
                if (CompletionEngine.ScanPrefixState(fullText, caretOffset, out string useDb))
                    return null;
                if (!string.IsNullOrEmpty(useDb) && connInfo != null &&
                    (catalog == null || !string.Equals(catalog.Database, useDb, StringComparison.OrdinalIgnoreCase)))
                {
                    var useCat = MetadataCatalogService.Instance.GetCachedCatalog(connInfo, useDb);
                    if (useCat != null)
                        catalog = useCat;
                }
                string cleanWord = hoverWord.Trim('[', ']', '"');
                if (string.IsNullOrEmpty(cleanWord)) return null;
                string dataSource = FormatDataSource(connInfo);
                string defaultDb = !string.IsNullOrEmpty(useDb) ? useDb : (connInfo?.Database ?? catalog?.Database);

                List<TSqlParserToken> tokens = null;
                LocalSymbols locals = null;
                TableRef fromRef = null;
                string slice = fullText;
                int localOffset = caretOffset;
                if (!string.IsNullOrEmpty(fullText))
                {
                    try
                    {
                        if (CompletionEngine.TryGetParseSlice(fullText, caretOffset, out slice, out int sliceStart))
                        {
                            localOffset = caretOffset - sliceStart;
                            if (localOffset < 0) localOffset = 0;
                            CompletionEngine.GetTokensForSlice(slice, out tokens);
                            if (tokens != null)
                            {
                                locals = CompletionEngine.CollectQueryLocalsFromTokens(tokens, localOffset, slice, fullText, caretOffset);
                                var hover = QuickInfoSqlContext.TryGetHoverToken(tokens, localOffset);
                                if (hover != null && string.Equals(hover.Name, "*", StringComparison.Ordinal))
                                    return TryBuildSelectStarQuickInfo(locals, hover.HasOwner ? hover.Owner : null, catalog, connInfo, dataSource, defaultDb);
                                if (hover != null && hover.HasOwner)
                                {
                                    var dcol = TryBuildDerivedColumnQuickInfo(locals, hover.Owner, hover.Name);
                                    if (dcol != null)
                                        return dcol;
                                    var aliasRef = QuickInfoSqlContext.TryResolveAlias(tokens, localOffset, hover.Owner)
                                                   ?? TryResolveAliasFromLocals(locals, hover.Owner);
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
                                    var hoverTable = QuickInfoSqlContext.TryResolveTableByHoverName(
                                        tokens, localOffset, hover.Name);
                                    if (hoverTable != null
                                        && string.Equals(hoverTable.Name, hover.Name, StringComparison.OrdinalIgnoreCase))
                                    {
                                        var ht = ResolveTable(connInfo, catalog, hoverTable);
                                        if (ht != null)
                                            return BuildTableQuickInfo(dataSource, defaultDb, hoverTable, ht);
                                    }
                                    var schemaTable = ResolveTable(connInfo, catalog, new TableRef
                                    {
                                        Schema = hover.Owner,
                                        Name = hover.Name,
                                        Database = hoverTable?.Database,
                                        LinkedServer = hoverTable?.LinkedServer
                                    });
                                    if (schemaTable != null)
                                        return BuildTableQuickInfo(dataSource, defaultDb, new TableRef
                                        {
                                            LinkedServer = hoverTable?.LinkedServer,
                                            Schema = schemaTable.Schema,
                                            Name = schemaTable.Name,
                                            Database = hoverTable?.Database ?? catalog?.Database
                                        }, schemaTable);
                                }
                                var derived = FindDerived(locals, cleanWord);
                                if (derived != null)
                                    return BuildDerivedTableQuickInfo(derived);
                                var aliasTable = TryBuildAliasTableQuickInfo(
                                    locals, cleanWord, catalog, connInfo, dataSource, defaultDb);
                                if (aliasTable != null)
                                    return aliasTable;
                                var fnCall = TryBuildBuiltInFunctionQuickInfo(cleanWord, tokens, localOffset, requireParen: true);
                                if (fnCall != null)
                                    return fnCall;
                                fromRef = QuickInfoSqlContext.TryResolveTableByHoverName(tokens, localOffset, cleanWord)
                                          ?? QuickInfoSqlContext.TryResolveFromTable(tokens, localOffset);
                            }
                        }
                    }
                    catch
                    {
                    }
                    if (fromRef == null)
                        fromRef = TryParseFromClause(slice);
                }

                if (string.Equals(cleanWord, "*", StringComparison.Ordinal))
                    return null;

                var procInfo = TryBuildProcedureQuickInfo(fullText, caretOffset, cleanWord, catalog, connInfo, dataSource, defaultDb);
                if (procInfo != null)
                    return procInfo;

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
                    Schema = fromRef != null && string.Equals(fromRef.Name, cleanWord, StringComparison.OrdinalIgnoreCase)
                        ? fromRef.Schema : null,
                    Database = fromRef != null && string.Equals(fromRef.Name, cleanWord, StringComparison.OrdinalIgnoreCase)
                        ? fromRef.Database : null,
                    LinkedServer = fromRef != null && string.Equals(fromRef.Name, cleanWord, StringComparison.OrdinalIgnoreCase)
                        ? fromRef.LinkedServer : null
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
                var fromCol = TryBuildUnqualifiedColumnQuickInfo(
                    locals, fromRef, cleanWord, catalog, connInfo, dataSource, defaultDb);
                if (fromCol != null)
                    return fromCol;
                var derivedCol = TryBuildDerivedColumnQuickInfo(locals, null, cleanWord);
                if (derivedCol != null)
                    return derivedCol;
                return TryBuildBuiltInFunctionQuickInfo(cleanWord, tokens, localOffset, requireParen: false);
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
                    LinkedServer = fromRef?.LinkedServer,
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
                    MetadataCatalogService.Instance.EnsureCatalogBuilding(connInfo, tref.Database);
                    target = MetadataCatalogService.Instance.GetCachedCatalogOrDisk(connInfo, tref.Database);
                    if (target == null)
                        return null;
                }
            }
            if (target != null)
            {
                var found = target.FindTableOrView(tref.Schema, tref.Name);
                if (found != null) return found;
            }
            return null;
        }

        private static TableRef TryResolveAliasFromLocals(LocalSymbols local, string alias)
        {
            if (local?.Aliases == null || string.IsNullOrEmpty(alias)) return null;
            TableRef tref;
            if (local.Aliases.TryGetValue(alias, out tref)
                && tref != null && !string.IsNullOrEmpty(tref.Name))
                return tref;
            return null;
        }

        private QuickInfoData TryBuildSelectStarQuickInfo(
            LocalSymbols local,
            string qualifier,
            MetadataCatalog catalog,
            ScriptFactoryAccess.ConnectionInfo connInfo,
            string dataSource,
            string defaultDb)
        {
            if (local?.Aliases == null || local.Aliases.Count == 0)
                return null;
            var sources = new List<KeyValuePair<string, TableRef>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in local.Aliases)
            {
                string alias = kv.Key;
                if (string.IsNullOrEmpty(alias) || alias.IndexOf('.') >= 0) continue;
                if (!string.IsNullOrEmpty(qualifier)
                    && !string.Equals(alias, qualifier, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (AliasIsRedundantTableName(local, alias, kv.Value)) continue;
                if (!seen.Add(alias)) continue;
                sources.Add(kv);
            }
            if (sources.Count == 0) return null;

            var sections = new List<Tuple<string, List<string>>>();
            int columnCount = 0;
            foreach (var src in sources)
            {
                var cols = TryGetSelectStarColumns(local, src.Key, src.Value, catalog, connInfo);
                if (cols == null || cols.Count == 0) continue;
                columnCount += cols.Count;
                sections.Add(Tuple.Create(FormatStarSourceHeading(src.Key, src.Value), cols));
            }

            var data = new QuickInfoData();
            AddHeaderLine(data, "类型", string.IsNullOrEmpty(qualifier) ? "SELECT *" : "SELECT " + qualifier + ".*");
            if (!string.IsNullOrEmpty(dataSource))
                AddHeaderLine(data, "数据源", dataSource);
            if (sections.Count == 1)
            {
                AddHeaderLine(data, "来源", sections[0].Item1);
                AddHeaderLine(data, "列数", columnCount.ToString());
                var sb = new System.Text.StringBuilder();
                foreach (var col in sections[0].Item2)
                    sb.AppendLine(col);
                data.DdlText = sb.ToString().TrimEnd();
            }
            else if (sections.Count > 1)
            {
                AddHeaderLine(data, "表数", sections.Count.ToString());
                AddHeaderLine(data, "列数", columnCount.ToString());
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < sections.Count; i++)
                {
                    if (i > 0) sb.AppendLine();
                    sb.AppendLine(sections[i].Item1);
                    foreach (var col in sections[i].Item2)
                        sb.Append("  ").AppendLine(col);
                }
                data.DdlText = sb.ToString().TrimEnd();
            }
            else
            {
                AddHeaderLine(data, "来源", FormatStarSourceHeading(sources[0].Key, sources[0].Value));
                data.DdlText = "列元数据未就绪";
            }
            return data.IsEmpty ? null : data;
        }

        private List<string> TryGetSelectStarColumns(
            LocalSymbols local,
            string alias,
            TableRef tref,
            MetadataCatalog catalog,
            ScriptFactoryAccess.ConnectionInfo connInfo)
        {
            var derived = FindDerived(local, alias);
            if (derived?.ColumnNames != null && derived.ColumnNames.Count > 0)
                return CopyNonEmptyNames(derived.ColumnNames);

            string objName = tref != null && !string.IsNullOrEmpty(tref.Name) ? tref.Name : alias;
            var tt = FindLocalTable(local?.TempTables, objName) ?? FindLocalTable(local?.TempTables, alias);
            if (tt?.ColumnNames != null && tt.ColumnNames.Count > 0)
                return CopyNonEmptyNames(tt.ColumnNames);
            var tv = FindLocalTable(local?.TableVariables, objName) ?? FindLocalTable(local?.TableVariables, alias);
            if (tv?.ColumnNames != null && tv.ColumnNames.Count > 0)
                return CopyNonEmptyNames(tv.ColumnNames);

            if (tref != null && !string.IsNullOrEmpty(tref.Name))
            {
                var byName = FindDerived(local, tref.Name);
                if (byName?.ColumnNames != null && byName.ColumnNames.Count > 0)
                    return CopyNonEmptyNames(byName.ColumnNames);
                var table = ResolveTable(connInfo, catalog, tref);
                if (table?.Columns != null && table.Columns.Count > 0)
                {
                    var cols = new List<string>(table.Columns.Count);
                    foreach (var c in table.Columns)
                    {
                        if (c != null && !string.IsNullOrEmpty(c.Name))
                            cols.Add(c.Name);
                    }
                    return cols;
                }
            }
            return null;
        }

        private static List<string> CopyNonEmptyNames(List<string> names)
        {
            var cols = new List<string>();
            if (names == null) return cols;
            foreach (var n in names)
            {
                if (!string.IsNullOrEmpty(n))
                    cols.Add(n);
            }
            return cols;
        }

        private static LocalTableInfo FindLocalTable(List<LocalTableInfo> list, string name)
        {
            if (list == null || string.IsNullOrEmpty(name)) return null;
            return list.Find(t => t != null && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string FormatStarSourceHeading(string alias, TableRef tref)
        {
            string table = FormatStarTableName(tref);
            if (string.IsNullOrEmpty(table))
                return alias;
            if (string.IsNullOrEmpty(alias)
                || string.Equals(alias, tref.Name, StringComparison.OrdinalIgnoreCase))
                return table;
            return alias + " (" + table + ")";
        }

        private static string FormatStarTableName(TableRef tref)
        {
            if (tref == null || string.IsNullOrEmpty(tref.Name)) return string.Empty;
            string schema = string.IsNullOrEmpty(tref.Schema) ? "dbo" : tref.Schema;
            if (!string.IsNullOrEmpty(tref.Database))
                return tref.Database + "." + schema + "." + tref.Name;
            return schema + "." + tref.Name;
        }

        private static bool AliasIsRedundantTableName(LocalSymbols local, string alias, TableRef tref)
        {
            if (local?.Aliases == null || tref == null || string.IsNullOrEmpty(tref.Name)) return false;
            if (!string.Equals(alias, tref.Name, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var kv in local.Aliases)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Key.IndexOf('.') >= 0) continue;
                if (string.Equals(kv.Key, alias, StringComparison.OrdinalIgnoreCase)) continue;
                if (SameStarTableRef(kv.Value, tref)) return true;
            }
            return false;
        }

        private static bool SameStarTableRef(TableRef a, TableRef b)
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

        private QuickInfoData TryBuildAliasTableQuickInfo(
            LocalSymbols local,
            string alias,
            MetadataCatalog catalog,
            ScriptFactoryAccess.ConnectionInfo connInfo,
            string dataSource,
            string defaultDb)
        {
            var tref = TryResolveAliasFromLocals(local, alias);
            if (tref == null) return null;
            var table = ResolveTable(connInfo, catalog, tref);
            return table == null ? null : BuildTableQuickInfo(dataSource, defaultDb, tref, table);
        }

        private QuickInfoData TryBuildAliasColumnQuickInfo(
            LocalSymbols local,
            string owner,
            string column,
            MetadataCatalog catalog,
            ScriptFactoryAccess.ConnectionInfo connInfo,
            string dataSource,
            string defaultDb)
        {
            var tref = TryResolveAliasFromLocals(local, owner);
            if (tref == null) return null;
            var table = ResolveTable(connInfo, catalog, tref);
            var col = FindColumn(table, column);
            return col == null ? null : BuildColumnQuickInfo(dataSource, defaultDb, tref, table, col);
        }

        private QuickInfoData TryBuildUnqualifiedColumnQuickInfo(
            LocalSymbols local,
            TableRef fromRef,
            string column,
            MetadataCatalog catalog,
            ScriptFactoryAccess.ConnectionInfo connInfo,
            string dataSource,
            string defaultDb)
        {
            if (string.IsNullOrEmpty(column)) return null;
            if (fromRef != null && !string.IsNullOrEmpty(fromRef.Name))
            {
                var table = ResolveTable(connInfo, catalog, fromRef);
                var col = FindColumn(table, column);
                if (col != null)
                    return BuildColumnQuickInfo(dataSource, defaultDb, fromRef, table, col);
            }
            if (local?.Aliases == null) return null;
            TableRef hitRef = null;
            TableColumnInfo hitTable = null;
            ColumnInfo hitCol = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in local.Aliases)
            {
                var tref = kv.Value;
                if (tref == null || string.IsNullOrEmpty(tref.Name)) continue;
                string key = (tref.Database ?? "") + "." + (tref.Schema ?? "") + "." + tref.Name;
                if (!seen.Add(key)) continue;
                var table = ResolveTable(connInfo, catalog, tref);
                var col = FindColumn(table, column);
                if (col == null) continue;
                if (hitCol != null) return null;
                hitCol = col;
                hitTable = table;
                hitRef = tref;
            }
            return hitCol == null ? null : BuildColumnQuickInfo(dataSource, defaultDb, hitRef, hitTable, hitCol);
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
            if (table != null && table.IsView && _hoverConn != null)
                MetadataCatalogService.Instance.EnsureViewDefinition(_hoverConn, db, table);
            data.DdlText = table != null && table.IsView
                ? QuickInfoDdlBuilder.BuildCreateView(table)
                : QuickInfoDdlBuilder.BuildCreateTable(table);
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

        private static CteInfo FindDerived(LocalSymbols local, string name)
        {
            if (local?.Ctes == null || string.IsNullOrEmpty(name)) return null;
            return local.Ctes.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool DerivedHasColumn(CteInfo derived, string column)
        {
            if (derived?.ColumnNames == null || string.IsNullOrEmpty(column)) return false;
            foreach (var c in derived.ColumnNames)
            {
                if (string.Equals(c, column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static QuickInfoData TryBuildDerivedColumnQuickInfo(LocalSymbols local, string owner, string column)
        {
            if (string.IsNullOrEmpty(column) || local?.Ctes == null) return null;
            if (!string.IsNullOrEmpty(owner))
            {
                var cte = FindDerived(local, owner);
                if (cte == null || !DerivedHasColumn(cte, column)) return null;
                return BuildDerivedColumnQuickInfo(cte, column);
            }
            CteInfo hit = null;
            foreach (var cte in local.Ctes)
            {
                if (!DerivedHasColumn(cte, column)) continue;
                if (hit != null) return null;
                hit = cte;
            }
            return hit == null ? null : BuildDerivedColumnQuickInfo(hit, column);
        }

        private static string GetDerivedColumnSql(CteInfo derived, string column)
        {
            if (derived?.ColumnNames == null || derived.ColumnSqls == null || string.IsNullOrEmpty(column))
                return null;
            int n = Math.Min(derived.ColumnNames.Count, derived.ColumnSqls.Count);
            for (int i = 0; i < n; i++)
            {
                if (string.Equals(derived.ColumnNames[i], column, StringComparison.OrdinalIgnoreCase))
                    return derived.ColumnSqls[i];
            }
            return null;
        }

        private static QuickInfoData BuildDerivedTableQuickInfo(CteInfo derived)
        {
            if (derived == null || string.IsNullOrEmpty(derived.Name)) return null;
            var data = new QuickInfoData();
            AddHeaderLine(data, "类型", "派生表");
            AddHeaderLine(data, "别名", derived.Name);
            if (!string.IsNullOrEmpty(derived.DefinitionSql))
            {
                data.DdlText = derived.DefinitionSql;
            }
            else if (derived.ColumnNames != null && derived.ColumnNames.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var c in derived.ColumnNames)
                {
                    if (!string.IsNullOrEmpty(c))
                        sb.AppendLine(c);
                }
                data.DdlText = sb.ToString().TrimEnd();
            }
            return data.IsEmpty ? null : data;
        }

        private static QuickInfoData BuildDerivedColumnQuickInfo(CteInfo derived, string column)
        {
            var data = new QuickInfoData();
            AddHeaderLine(data, "类型", "派生表列");
            AddHeaderLine(data, "派生表", derived?.Name);
            AddHeaderLine(data, "列", column);
            string sql = GetDerivedColumnSql(derived, column);
            data.DdlText = !string.IsNullOrEmpty(sql) ? sql : null;
            return data.IsEmpty ? null : data;
        }

        private static QuickInfoData TryBuildBuiltInFunctionQuickInfo(
            string name, List<TSqlParserToken> tokens, int localOffset, bool requireParen)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string displayName;
            string signature;
            string[] parameters;
            if (!CompletionEngine.TryGetBuiltInFunction(name, out displayName, out signature, out parameters))
                return null;
            bool followedByParen = tokens != null && QuickInfoSqlContext.NextSignificantIsLeftParen(tokens, localOffset);
            if (requireParen)
            {
                if (!followedByParen) return null;
            }
            else if (!followedByParen && signature != null && signature.IndexOf('(') >= 0)
            {
                // 无括号兜底只给 CURRENT_TIMESTAMP 等；避免 LEFT JOIN 的 LEFT 被当成函数
                return null;
            }
            var data = new QuickInfoData();
            AddHeaderLine(data, "类型", "内建函数");
            AddHeaderLine(data, "函数", displayName);
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(signature))
                sb.Append(signature);
            if (parameters != null && parameters.Length > 0)
            {
                if (sb.Length > 0) sb.AppendLine().AppendLine();
                sb.AppendLine("参数:");
                foreach (var p in parameters)
                    sb.Append("  ").AppendLine(p);
            }
            data.DdlText = sb.ToString().TrimEnd();
            return data.IsEmpty ? null : data;
        }

        private static void AddHeaderLine(QuickInfoData data, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            data.HeaderLines.Add(label + ": " + value);
        }
    }
}

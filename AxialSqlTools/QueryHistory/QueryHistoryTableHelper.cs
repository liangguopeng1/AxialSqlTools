using Microsoft.Data.SqlClient;
using System;

namespace AxialSqlTools
{
    internal static class QueryHistoryTableHelper
    {
        public const string DefaultTableName = "[dbo].[QueryHistory]";

        public static string ResolveTableName(string configuredName)
        {
            if (string.IsNullOrWhiteSpace(configuredName))
            {
                return DefaultTableName;
            }

            return configuredName.Trim();
        }

        public static string BuildEnsureTableSql(string tableName)
        {
            string resolved = ResolveTableName(tableName);
            string indexSuffix = Guid.NewGuid().ToString("N");
            return $@"
IF OBJECT_ID(N'{resolved}', N'U') IS NULL
BEGIN
    CREATE TABLE {resolved} (
        [QueryID]           INT            IDENTITY (1, 1) NOT NULL,
        [StartTime]         DATETIME       NOT NULL,
        [FinishTime]        DATETIME       NOT NULL,
        [ElapsedTime]       VARCHAR (15)   NOT NULL,
        [TotalRowsReturned] BIGINT         NOT NULL,
        [ExecResult]        VARCHAR (100)  NOT NULL,
        [QueryText]         NVARCHAR (MAX) NOT NULL,
        [DataSource]        NVARCHAR (128) NOT NULL,
        [DatabaseName]      NVARCHAR (128) NOT NULL,
        [LoginName]         NVARCHAR (128) NOT NULL,
        [WorkstationId]     NVARCHAR (128) NOT NULL,
        PRIMARY KEY CLUSTERED ([QueryID]),
        INDEX [IDX_{indexSuffix}_1] ([StartTime]),
        INDEX [IDX_{indexSuffix}_2] ([FinishTime]),
        INDEX [IDX_{indexSuffix}_3] ([DataSource]),
        INDEX [IDX_{indexSuffix}_4] ([DatabaseName])
    );
    ALTER INDEX ALL ON {resolved} REBUILD WITH (DATA_COMPRESSION = PAGE);
END
";
        }

        public static void EnsureTableExists(SqlConnection connection, string tableName)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            using (var cmd = new SqlCommand(BuildEnsureTableSql(tableName), connection))
            {
                cmd.CommandTimeout = 60;
                cmd.ExecuteNonQuery();
            }
        }
    }
}

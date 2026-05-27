using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;
using Npgsql;
using NpgsqlTypes;

namespace PostgresMcpBridge.Tools;

/// <summary>
/// MCP tools that bridge to a remote PostgreSQL database.
/// Each tool receives the shared <see cref="NpgsqlDataSource"/> via DI.
/// </summary>
[McpServerToolType]
public static class DatabaseTools
{
    // Safety cap so a huge result set cannot overwhelm the MCP client.
    private const int MaxRows = 10_000;

    // Default per-command timeout (seconds) when the caller does not specify one.
    private const int DefaultTimeoutSeconds = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    [McpServerTool(Name = "execute_query")]
    [Description(
        "Executes a raw SQL query against the connected GaussDB SQL database and returns the " +
        "result rows as JSON. For statements that return no result set (e.g. INSERT/UPDATE/DELETE " +
        "without RETURNING) it returns the number of affected rows.")]
    public static async Task<string> ExecuteQuery(
        NpgsqlDataSource dataSource,
        [Description("The raw SQL query to execute.")] string rawQuery,
        [Description("Command timeout in seconds for this query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(rawQuery, connection)
            {
                CommandTimeout = timeoutSeconds
            };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // No columns => this was a non-query statement.
            if (reader.FieldCount == 0)
            {
                stopwatch.Stop();
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    rowsAffected = reader.RecordsAffected,
                    timeTaken = stopwatch.Elapsed.ToString(),
                    rows = Array.Empty<object>()
                }, JsonOptions);
            }

            var columns = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                columns[i] = reader.GetName(i);

            var rows = new List<Dictionary<string, object?>>();
            var truncated = false;

            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count >= MaxRows)
                {
                    truncated = true;
                    break;
                }

                var row = new Dictionary<string, object?>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[columns[i]] = ConvertValue(reader, i);
                rows.Add(row);
            }

            stopwatch.Stop();
            return JsonSerializer.Serialize(new
            {
                success = true,
                rowCount = rows.Count,
                truncated,
                timeTaken = stopwatch.Elapsed.ToString(),
                columns,
                rows
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "get_indexes")]
    [Description(
        "Gets all indexes defined on the given table, including the index name, its full " +
        "definition, whether it is unique or the primary key, the access method, and the columns " +
        "involved. The table name may be schema-qualified (e.g. 'public.users').")]
    public static async Task<string> GetIndexes(
        NpgsqlDataSource dataSource,
        [Description("The name of the table to inspect. May be schema-qualified (e.g. 'public.users') or just the table name.")] string tableName,
        [Description("Command timeout in seconds for this query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                n.nspname                       AS schema_name,
                t.relname                       AS table_name,
                i.relname                       AS index_name,
                ix.indisunique                  AS is_unique,
                ix.indisprimary                 AS is_primary,
                am.amname                       AS index_type,
                pg_get_indexdef(ix.indexrelid)  AS index_definition,
                array_to_string(array_agg(a.attname ORDER BY x.ord), ', ') AS columns
            FROM pg_index ix
            JOIN pg_class i      ON i.oid = ix.indexrelid
            JOIN pg_class t      ON t.oid = ix.indrelid
            JOIN pg_namespace n  ON n.oid = t.relnamespace
            JOIN pg_am am        ON am.oid = i.relam
            JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS x(attnum, ord) ON TRUE
            LEFT JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = x.attnum
            WHERE t.relname = @table
              AND (@schema IS NULL OR n.nspname = @schema)
            GROUP BY n.nspname, t.relname, i.relname, ix.indisunique, ix.indisprimary, am.amname, ix.indexrelid
            ORDER BY i.relname;
            """;

        var (schema, table) = SplitTableName(tableName);

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var indexes = await ReadRowsAsync(connection, sql, timeoutSeconds, cancellationToken,
                ("table", table), ("schema", schema));

            return JsonSerializer.Serialize(new
            {
                success = true,
                table = tableName,
                indexCount = indexes.Count,
                indexes
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "check_query_speed")]
    [Description(
        "Measures the performance of a query. It runs EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) and " +
        "reports GaussDB SQL's planning time, execution time and the total client round-trip time " +
        "in milliseconds, along with the full execution plan. The query runs inside a transaction " +
        "that is always rolled back, so write statements do not persist any changes.")]
    public static async Task<string> CheckQuerySpeed(
        NpgsqlDataSource dataSource,
        [Description("The SQL query whose performance should be measured.")] string query,
        [Description("Command timeout in seconds for this query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using var command = new NpgsqlCommand(
                $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) {query}", connection, transaction)
            {
                CommandTimeout = timeoutSeconds
            };

            var stopwatch = Stopwatch.StartNew();
            var raw = await command.ExecuteScalarAsync(cancellationToken);
            stopwatch.Stop();

            // Never persist side effects of ANALYZE for write queries.
            await transaction.RollbackAsync(cancellationToken);

            var planJson = raw?.ToString() ?? "[]";
            using var planDoc = JsonDocument.Parse(planJson);
            var root = planDoc.RootElement[0];

            double? planningTime = root.TryGetProperty("Planning Time", out var p) ? p.GetDouble() : null;
            double? executionTime = root.TryGetProperty("Execution Time", out var e) ? e.GetDouble() : null;

            return JsonSerializer.Serialize(new
            {
                success = true,
                planningTimeMs = planningTime,
                executionTimeMs = executionTime,
                totalRoundTripMs = stopwatch.Elapsed.TotalMilliseconds,
                plan = root
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "list_tables")]
    [Description(
        "Lists all user tables in the database (excluding system catalogs), with their schema, " +
        "type, total on-disk size and estimated row count.")]
    public static async Task<string> ListTables(
        NpgsqlDataSource dataSource,
        [Description("Command timeout in seconds for this query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                n.nspname                                     AS schema_name,
                c.relname                                     AS table_name,
                CASE c.relkind WHEN 'r' THEN 'table' WHEN 'p' THEN 'partitioned table' END AS type,
                pg_size_pretty(pg_total_relation_size(c.oid)) AS total_size,
                c.reltuples::bigint                           AS estimated_rows
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p')
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            ORDER BY n.nspname, c.relname;
            """;

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var tables = await ReadRowsAsync(connection, sql, timeoutSeconds, cancellationToken);

            return JsonSerializer.Serialize(new
            {
                success = true,
                tableCount = tables.Count,
                tables
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "describe_schema")]
    [Description(
        "Describes the schema of a table: every column with its data type, nullability, default " +
        "value and ordinal position, plus all table constraints (primary key, foreign keys, " +
        "unique, check). The table name may be schema-qualified (e.g. 'public.users').")]
    public static async Task<string> DescribeSchema(
        NpgsqlDataSource dataSource,
        [Description("The name of the table to describe. May be schema-qualified (e.g. 'public.users') or just the table name.")] string tableName,
        [Description("Command timeout in seconds for this query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        const string columnsSql = """
            SELECT
                a.attname                            AS column_name,
                format_type(a.atttypid, a.atttypmod) AS data_type,
                NOT a.attnotnull                     AS is_nullable,
                pg_get_expr(d.adbin, d.adrelid)      AS default_value,
                a.attnum                             AS ordinal_position
            FROM pg_attribute a
            JOIN pg_class c     ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE c.relname = @table
              AND (@schema IS NULL OR n.nspname = @schema)
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY a.attnum;
            """;

        const string constraintsSql = """
            SELECT
                con.conname AS constraint_name,
                CASE con.contype
                    WHEN 'p' THEN 'primary key'
                    WHEN 'f' THEN 'foreign key'
                    WHEN 'u' THEN 'unique'
                    WHEN 'c' THEN 'check'
                    WHEN 'x' THEN 'exclusion'
                    ELSE con.contype::text
                END AS constraint_type,
                pg_get_constraintdef(con.oid) AS definition
            FROM pg_constraint con
            JOIN pg_class c     ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relname = @table
              AND (@schema IS NULL OR n.nspname = @schema)
            ORDER BY con.contype, con.conname;
            """;

        var (schema, table) = SplitTableName(tableName);

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var columns = await ReadRowsAsync(connection, columnsSql, timeoutSeconds, cancellationToken,
                ("table", table), ("schema", schema));
            var constraints = await ReadRowsAsync(connection, constraintsSql, timeoutSeconds, cancellationToken,
                ("table", table), ("schema", schema));

            return JsonSerializer.Serialize(new
            {
                success = true,
                table = tableName,
                columnCount = columns.Count,
                columns,
                constraints
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "check_bottleneck")]
    [Description(
        "Runs a database-wide performance health check and reports common bottlenecks: overall " +
        "cache hit ratio, tables with heavy sequential scans (possible missing indexes), tables " +
        "with many dead tuples (vacuum/bloat), unused non-constraint indexes, currently " +
        "long-running queries (>1s), and queries blocked by locks. Read-only; nothing is modified.")]
    public static async Task<string> CheckBottleneck(
        NpgsqlDataSource dataSource,
        [Description("Command timeout in seconds for each diagnostic query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        const string cacheHitSql = """
            SELECT
                sum(heap_blks_read) AS heap_blocks_read,
                sum(heap_blks_hit)  AS heap_blocks_hit,
                CASE WHEN sum(heap_blks_hit) + sum(heap_blks_read) = 0 THEN NULL
                     ELSE round(sum(heap_blks_hit)::numeric
                          / (sum(heap_blks_hit) + sum(heap_blks_read)) * 100, 2)
                END AS cache_hit_ratio_pct
            FROM pg_statio_user_tables;
            """;

        const string seqScanSql = """
            SELECT schemaname AS schema_name, relname AS table_name,
                   seq_scan, seq_tup_read, idx_scan, n_live_tup AS estimated_rows
            FROM pg_stat_user_tables
            WHERE seq_scan > 0
            ORDER BY seq_scan DESC
            LIMIT 10;
            """;

        const string deadTupSql = """
            SELECT schemaname AS schema_name, relname AS table_name,
                   n_live_tup, n_dead_tup,
                   CASE WHEN n_live_tup > 0
                        THEN round(n_dead_tup::numeric / n_live_tup * 100, 2) END AS dead_tuple_pct,
                   last_autovacuum
            FROM pg_stat_user_tables
            WHERE n_dead_tup > 0
            ORDER BY n_dead_tup DESC
            LIMIT 10;
            """;

        const string unusedIdxSql = """
            SELECT s.schemaname AS schema_name, s.relname AS table_name,
                   s.indexrelname AS index_name, s.idx_scan,
                   pg_size_pretty(pg_relation_size(s.indexrelid)) AS index_size
            FROM pg_stat_user_indexes s
            JOIN pg_index i ON i.indexrelid = s.indexrelid
            WHERE s.idx_scan = 0
              AND NOT i.indisprimary
              AND NOT i.indisunique
            ORDER BY pg_relation_size(s.indexrelid) DESC
            LIMIT 10;
            """;

        const string longRunningSql = """
            SELECT pid, usename AS username, state,
                   round(extract(epoch FROM (now() - query_start))::numeric, 1) AS runtime_seconds,
                   wait_event_type, wait_event,
                   left(query, 200) AS query
            FROM pg_stat_activity
            WHERE state <> 'idle'
              AND query_start IS NOT NULL
              AND pid <> pg_backend_pid()
              AND now() - query_start > interval '1 second'
            ORDER BY query_start ASC
            LIMIT 10;
            """;

        const string blockingSql = """
            SELECT blocked.pid  AS blocked_pid,
                   blocking.pid AS blocking_pid,
                   left(blocked.query, 150)  AS blocked_query,
                   left(blocking.query, 150) AS blocking_query
            FROM pg_stat_activity blocked
            JOIN pg_stat_activity blocking
              ON blocking.pid = ANY (pg_blocking_pids(blocked.pid))
            WHERE cardinality(pg_blocking_pids(blocked.pid)) > 0
            LIMIT 10;
            """;

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

            var cacheRows = await ReadRowsAsync(connection, cacheHitSql, timeoutSeconds, cancellationToken);

            return JsonSerializer.Serialize(new
            {
                success = true,
                cacheHitRatio = cacheRows.Count > 0 ? cacheRows[0] : null,
                tablesWithHighSequentialScans = await ReadRowsAsync(connection, seqScanSql, timeoutSeconds, cancellationToken),
                tablesNeedingVacuum = await ReadRowsAsync(connection, deadTupSql, timeoutSeconds, cancellationToken),
                unusedIndexes = await ReadRowsAsync(connection, unusedIdxSql, timeoutSeconds, cancellationToken),
                longRunningQueries = await ReadRowsAsync(connection, longRunningSql, timeoutSeconds, cancellationToken),
                blockingLocks = await ReadRowsAsync(connection, blockingSql, timeoutSeconds, cancellationToken)
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "explain_query")]
    [Description(
        "Returns the GaussDB SQL execution plan for a query WITHOUT running it (plain EXPLAIN, no " +
        "ANALYZE), so it is safe for expensive or write statements. Reports the planner's " +
        "estimated total cost, estimated row count and the full plan tree.")]
    public static async Task<string> ExplainQuery(
        NpgsqlDataSource dataSource,
        [Description("The SQL query to explain. It is analyzed by the planner but never executed.")] string query,
        [Description("Command timeout in seconds for this query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"EXPLAIN (VERBOSE, FORMAT JSON) {query}", connection)
            {
                CommandTimeout = timeoutSeconds
            };

            var raw = await command.ExecuteScalarAsync(cancellationToken);
            var planJson = raw?.ToString() ?? "[]";
            using var planDoc = JsonDocument.Parse(planJson);
            var root = planDoc.RootElement[0];

            double? totalCost = null;
            double? estimatedRows = null;
            string? topNode = null;
            if (root.TryGetProperty("Plan", out var plan))
            {
                if (plan.TryGetProperty("Total Cost", out var tc)) totalCost = tc.GetDouble();
                if (plan.TryGetProperty("Plan Rows", out var pr)) estimatedRows = pr.GetDouble();
                if (plan.TryGetProperty("Node Type", out var nt)) topNode = nt.GetString();
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                topNodeType = topNode,
                estimatedTotalCost = totalCost,
                estimatedRows,
                plan = root
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "find_columns")]
    [Description(
        "Searches every user table, view and materialized view for columns whose name matches the " +
        "given pattern (case-insensitive). Useful for tracking down where a field lives across a " +
        "large or legacy schema. A bare word is matched as a substring; supply '%' wildcards for " +
        "custom patterns.")]
    public static async Task<string> FindColumns(
        NpgsqlDataSource dataSource,
        [Description("Column name or pattern to search for, e.g. 'status' or 'cust%id'.")] string namePattern,
        [Description("Command timeout in seconds for this query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                n.nspname                            AS schema_name,
                c.relname                            AS table_name,
                CASE c.relkind
                    WHEN 'r' THEN 'table'
                    WHEN 'p' THEN 'partitioned table'
                    WHEN 'v' THEN 'view'
                    WHEN 'm' THEN 'materialized view'
                END AS object_type,
                a.attname                            AS column_name,
                format_type(a.atttypid, a.atttypmod) AS data_type,
                NOT a.attnotnull                     AS is_nullable
            FROM pg_attribute a
            JOIN pg_class c     ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'v', 'm')
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND a.attname ILIKE @pattern
            ORDER BY n.nspname, c.relname, a.attnum;
            """;

        var like = namePattern.Contains('%') ? namePattern : $"%{namePattern}%";

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var matches = await ReadRowsAsync(connection, sql, timeoutSeconds, cancellationToken,
                ("pattern", like));

            return JsonSerializer.Serialize(new
            {
                success = true,
                pattern = namePattern,
                matchCount = matches.Count,
                matches
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "list_functions")]
    [Description(
        "Lists stored functions and procedures defined in user schemas, with their argument list, " +
        "return type, language and kind. Useful for inventorying legacy business logic that lives " +
        "in the database. Optionally restrict to a single schema.")]
    public static async Task<string> ListFunctions(
        NpgsqlDataSource dataSource,
        [Description("Optional schema to restrict to (e.g. 'public'). Leave empty for all user schemas.")] string? schema = null,
        [Description("Command timeout in seconds for this query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT n.nspname                        AS schema_name,
                   p.proname                        AS function_name,
                   pg_get_function_arguments(p.oid) AS arguments,
                   pg_get_function_result(p.oid)    AS return_type,
                   l.lanname                        AS language,
                   CASE p.prokind
                       WHEN 'f' THEN 'function'
                       WHEN 'p' THEN 'procedure'
                       WHEN 'a' THEN 'aggregate'
                       WHEN 'w' THEN 'window'
                   END AS kind
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            JOIN pg_language l  ON l.oid = p.prolang
            WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND (@schema IS NULL OR n.nspname = @schema)
            ORDER BY n.nspname, p.proname;
            """;

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var functions = await ReadRowsAsync(connection, sql, timeoutSeconds, cancellationToken,
                ("schema", string.IsNullOrWhiteSpace(schema) ? null : schema));

            return JsonSerializer.Serialize(new
            {
                success = true,
                functionCount = functions.Count,
                functions
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "get_function_definition")]
    [Description(
        "Returns the full CREATE statement / source code of a stored function or procedure via " +
        "pg_get_functiondef. If the name is overloaded, every matching definition is returned. " +
        "The name may be schema-qualified (e.g. 'public.calculate_total').")]
    public static async Task<string> GetFunctionDefinition(
        NpgsqlDataSource dataSource,
        [Description("The function or procedure name. May be schema-qualified (e.g. 'public.calculate_total').")] string functionName,
        [Description("Command timeout in seconds for this query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT n.nspname                        AS schema_name,
                   p.proname                        AS function_name,
                   pg_get_function_arguments(p.oid) AS arguments,
                   pg_get_functiondef(p.oid)        AS definition
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE p.proname = @name
              AND p.prokind IN ('f', 'p')
              AND (@schema IS NULL OR n.nspname = @schema)
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            ORDER BY n.nspname, p.proname;
            """;

        var (schema, name) = SplitTableName(functionName);

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var definitions = await ReadRowsAsync(connection, sql, timeoutSeconds, cancellationToken,
                ("name", name), ("schema", schema));

            return JsonSerializer.Serialize(new
            {
                success = true,
                function = functionName,
                matchCount = definitions.Count,
                definitions
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    /// <summary>
    /// Reads a value from the reader, mapping DBNull to null and keeping JSON-friendly
    /// CLR types as-is; anything exotic is rendered via ToString().
    /// </summary>
    private static object? ConvertValue(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool or string or byte[] or DateTime or DateTimeOffset or Guid
                or sbyte or byte or short or ushort or int or uint or long or ulong
                or float or double or decimal => value,
            _ => value.ToString()
        };
    }

    /// <summary>
    /// Executes <paramref name="sql"/> with optional text parameters and materializes
    /// every row into a name/value dictionary.
    /// </summary>
    private static async Task<List<Dictionary<string, object?>>> ReadRowsAsync(
        NpgsqlConnection connection,
        string sql,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        params (string name, string? value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = timeoutSeconds
        };
        foreach (var (name, value) in parameters)
            command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
            {
                Value = (object?)value ?? DBNull.Value
            });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = ConvertValue(reader, i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Splits an optionally schema-qualified name like "public.users" into (schema, table).
    /// Returns a null schema when no schema is given.
    /// </summary>
    private static (string? schema, string table) SplitTableName(string tableName)
    {
        var dot = tableName.IndexOf('.');
        return dot > 0
            ? (tableName[..dot], tableName[(dot + 1)..])
            : (null, tableName);
    }

    private static string Error(Exception ex) =>
        JsonSerializer.Serialize(new
        {
            success = false,
            error = ex.Message,
            errorType = ex.GetType().Name
        }, JsonOptions);
}

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

    [McpServerTool(Name = "check_foreign_key")]
    [Description(
        "Checks for orphaned rows when a logical (un-enforced) foreign-key relationship exists. " +
        "Counts rows in childTable whose childColumn value is not present in " +
        "parentTable.parentColumn, and returns a sample of orphan values. Useful for auditing " +
        "referential integrity in legacy schemas where FK constraints were never declared.")]
    public static async Task<string> CheckForeignKey(
        NpgsqlDataSource dataSource,
        [Description("Child table (the one whose values reference the parent). May be schema-qualified.")] string childTable,
        [Description("Column on the child table that should reference the parent.")] string childColumn,
        [Description("Parent table whose column values are the reference set. May be schema-qualified.")] string parentTable,
        [Description("Column on the parent table that holds the reference values (usually its primary key).")] string parentColumn,
        [Description("Command timeout in seconds for each query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var childTableQ  = QualifiedQuoted(childTable);
        var parentTableQ = QualifiedQuoted(parentTable);
        var childColQ    = QuoteIdent(childColumn);
        var parentColQ   = QuoteIdent(parentColumn);

        var countSql =
            $"SELECT COUNT(*) FROM {childTableQ} c " +
            $"WHERE c.{childColQ} IS NOT NULL " +
            $"  AND NOT EXISTS (SELECT 1 FROM {parentTableQ} p WHERE p.{parentColQ} = c.{childColQ});";

        var sampleSql =
            $"SELECT DISTINCT c.{childColQ} AS orphan_value FROM {childTableQ} c " +
            $"WHERE c.{childColQ} IS NOT NULL " +
            $"  AND NOT EXISTS (SELECT 1 FROM {parentTableQ} p WHERE p.{parentColQ} = c.{childColQ}) " +
            $"LIMIT 100;";

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

            await using var countCmd = new NpgsqlCommand(countSql, connection)
            {
                CommandTimeout = timeoutSeconds
            };
            var orphanCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(cancellationToken) ?? 0L);

            var sample = await ReadRowsAsync(connection, sampleSql, timeoutSeconds, cancellationToken);

            return JsonSerializer.Serialize(new
            {
                success = true,
                childTable,
                childColumn,
                parentTable,
                parentColumn,
                orphanCount,
                isValid = orphanCount == 0,
                sampleOrphans = sample
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "list_triggers")]
    [Description(
        "Lists triggers in user schemas, decoded from pg_trigger: timing (BEFORE / AFTER / INSTEAD OF), " +
        "events (INSERT / UPDATE / DELETE / TRUNCATE), row vs. statement level, the function called, " +
        "whether the trigger is enabled, and the full pg_get_triggerdef text. Internal constraint " +
        "triggers are filtered out. Triggers are a common hiding place for legacy business logic.")]
    public static async Task<string> ListTriggers(
        NpgsqlDataSource dataSource,
        [Description("Optional table to restrict to (may be schema-qualified). Leave empty for all user tables.")] string? tableName = null,
        [Description("Command timeout in seconds for this query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT n.nspname AS schema_name,
                   c.relname AS table_name,
                   t.tgname  AS trigger_name,
                   CASE
                       WHEN (t.tgtype & 2)  <> 0 THEN 'BEFORE'
                       WHEN (t.tgtype & 64) <> 0 THEN 'INSTEAD OF'
                       ELSE 'AFTER'
                   END AS timing,
                   trim(trailing ' ' FROM
                       (CASE WHEN (t.tgtype & 4)  <> 0 THEN 'INSERT '   ELSE '' END ||
                        CASE WHEN (t.tgtype & 8)  <> 0 THEN 'DELETE '   ELSE '' END ||
                        CASE WHEN (t.tgtype & 16) <> 0 THEN 'UPDATE '   ELSE '' END ||
                        CASE WHEN (t.tgtype & 32) <> 0 THEN 'TRUNCATE ' ELSE '' END)
                   ) AS events,
                   CASE WHEN (t.tgtype & 1) <> 0 THEN 'ROW' ELSE 'STATEMENT' END AS level,
                   p.proname AS function_name,
                   t.tgenabled <> 'D' AS enabled,
                   pg_get_triggerdef(t.oid) AS definition
            FROM pg_trigger t
            JOIN pg_class c     ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_proc p      ON p.oid = t.tgfoid
            WHERE NOT t.tgisinternal
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND (@schema IS NULL OR n.nspname = @schema)
              AND (@table  IS NULL OR c.relname = @table)
            ORDER BY n.nspname, c.relname, t.tgname;
            """;

        string? schema = null, table = null;
        if (!string.IsNullOrWhiteSpace(tableName))
        {
            var split = SplitTableName(tableName);
            schema = split.schema;
            table  = split.table;
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var triggers = await ReadRowsAsync(connection, sql, timeoutSeconds, cancellationToken,
                ("schema", schema), ("table", table));

            return JsonSerializer.Serialize(new
            {
                success = true,
                triggerCount = triggers.Count,
                triggers
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "find_tables_without_primary_key")]
    [Description(
        "Returns every user table that has no primary-key constraint, ordered by size descending. " +
        "Missing primary keys make replication, deduplication and ORM mapping risky, so this is " +
        "one of the first things to audit when inheriting a legacy database.")]
    public static async Task<string> FindTablesWithoutPrimaryKey(
        NpgsqlDataSource dataSource,
        [Description("Optional schema to restrict to. Leave empty for all user schemas.")] string? schema = null,
        [Description("Command timeout in seconds for this query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT n.nspname                                     AS schema_name,
                   c.relname                                     AS table_name,
                   pg_size_pretty(pg_total_relation_size(c.oid)) AS total_size,
                   c.reltuples::bigint                           AS estimated_rows
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p')
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND (@schema IS NULL OR n.nspname = @schema)
              AND NOT EXISTS (
                  SELECT 1 FROM pg_constraint con
                  WHERE con.conrelid = c.oid AND con.contype = 'p'
              )
            ORDER BY pg_total_relation_size(c.oid) DESC;
            """;

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var tables = await ReadRowsAsync(connection, sql, timeoutSeconds, cancellationToken,
                ("schema", string.IsNullOrWhiteSpace(schema) ? null : schema));

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

    [McpServerTool(Name = "sample_table")]
    [Description(
        "Returns the first N rows from a table — a quick way to see what data actually lives in a " +
        "legacy table without writing a SELECT. Table name may be schema-qualified. The limit is " +
        "clamped between 1 and 1000.")]
    public static async Task<string> SampleTable(
        NpgsqlDataSource dataSource,
        [Description("Table to sample. May be schema-qualified (e.g. 'public.users').")] string tableName,
        [Description("Number of rows to return (default 10, clamped to 1..1000).")] int limit = 10,
        [Description("Command timeout in seconds for this query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1) limit = 1;
        if (limit > 1000) limit = 1000;

        var tableQ = QualifiedQuoted(tableName);
        var sql = $"SELECT * FROM {tableQ} LIMIT @limit;";

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection)
            {
                CommandTimeout = timeoutSeconds
            };
            command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var columns = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                columns[i] = reader.GetName(i);

            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[columns[i]] = ConvertValue(reader, i);
                rows.Add(row);
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                table = tableName,
                rowCount = rows.Count,
                columns,
                rows
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "get_column_stats")]
    [Description(
        "Profiles a single column: total row count, non-null count, null count, distinct count, " +
        "and MIN / MAX values (cast to text). Helpful for assessing data quality in legacy tables. " +
        "Can be expensive on large tables — relies on the per-call timeoutSeconds.")]
    public static async Task<string> GetColumnStats(
        NpgsqlDataSource dataSource,
        [Description("Table containing the column. May be schema-qualified.")] string tableName,
        [Description("Column name to profile.")] string columnName,
        [Description("Command timeout in seconds for this query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var tableQ = QualifiedQuoted(tableName);
        var colQ   = QuoteIdent(columnName);

        var sql =
            $"SELECT COUNT(*)                  AS total_count, " +
            $"       COUNT({colQ})             AS non_null_count, " +
            $"       COUNT(*) - COUNT({colQ})  AS null_count, " +
            $"       COUNT(DISTINCT {colQ})    AS distinct_count, " +
            $"       MIN({colQ})::text         AS min_value, " +
            $"       MAX({colQ})::text         AS max_value " +
            $"FROM {tableQ};";

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var rows = await ReadRowsAsync(connection, sql, timeoutSeconds, cancellationToken);

            return JsonSerializer.Serialize(new
            {
                success = true,
                table = tableName,
                column = columnName,
                stats = rows.Count > 0 ? rows[0] : null
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "get_table_dependencies")]
    [Description(
        "Lists views and materialized views that depend on the given table via pg_depend / " +
        "pg_rewrite. Run this before altering or dropping a legacy table to see what will break.")]
    public static async Task<string> GetTableDependencies(
        NpgsqlDataSource dataSource,
        [Description("Table to inspect. May be schema-qualified.")] string tableName,
        [Description("Command timeout in seconds for this query. 0 means no timeout. Defaults to 500.")] int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT DISTINCT
                dn.nspname AS dependent_schema,
                dc.relname AS dependent_object,
                CASE dc.relkind
                    WHEN 'v' THEN 'view'
                    WHEN 'm' THEN 'materialized view'
                    WHEN 'r' THEN 'table'
                    WHEN 'i' THEN 'index'
                    WHEN 'S' THEN 'sequence'
                    ELSE dc.relkind::text
                END AS dependent_type
            FROM pg_depend d
            JOIN pg_rewrite r    ON r.oid  = d.objid
            JOIN pg_class dc     ON dc.oid = r.ev_class
            JOIN pg_namespace dn ON dn.oid = dc.relnamespace
            JOIN pg_class tc     ON tc.oid = d.refobjid
            JOIN pg_namespace tn ON tn.oid = tc.relnamespace
            WHERE d.classid    = 'pg_rewrite'::regclass
              AND d.refclassid = 'pg_class'::regclass
              AND tc.relname = @table
              AND (@schema IS NULL OR tn.nspname = @schema)
              AND dc.oid <> tc.oid
            ORDER BY dn.nspname, dc.relname;
            """;

        var (schema, table) = SplitTableName(tableName);

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var deps = await ReadRowsAsync(connection, sql, timeoutSeconds, cancellationToken,
                ("table", table), ("schema", schema));

            return JsonSerializer.Serialize(new
            {
                success = true,
                table = tableName,
                dependencyCount = deps.Count,
                dependencies = deps
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    /// <summary>
    /// Double-quotes an identifier so it can be safely interpolated into SQL.
    /// Embedded double quotes are escaped per the SQL standard.
    /// </summary>
    private static string QuoteIdent(string ident) =>
        "\"" + ident.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// Quotes a (possibly schema-qualified) relation name for safe interpolation.
    /// </summary>
    private static string QualifiedQuoted(string fqName)
    {
        var (schema, name) = SplitTableName(fqName);
        return schema is null ? QuoteIdent(name) : QuoteIdent(schema) + "." + QuoteIdent(name);
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

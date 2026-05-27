# PostgresMcpBridge

A .NET 10 / C# 14 ASP.NET Core application that exposes a remote PostgreSQL database to MCP (Model Context Protocol) clients over **Streamable HTTP**. It packages ten tools — raw query execution, schema introspection, performance diagnostics and stored-function inspection — so an MCP-capable client (e.g. an LLM) can inspect and operate on a Postgres instance through a single HTTP endpoint.

## Architecture

The application is a self-contained ASP.NET Core web host that hosts an MCP server via `ModelContextProtocol.AspNetCore`. At startup it:

1. Reads `ConnectionStrings:Postgres` from `appsettings.json` (or the `ConnectionStrings__Postgres` environment variable).
2. Creates one shared `NpgsqlDataSource` (an Npgsql connection pool) and registers it as a DI singleton.
3. Registers the MCP server with `AddMcpServer().WithHttpTransport().WithToolsFromAssembly()`. The last call reflects over the executing assembly and registers every `[McpServerToolType]` class and every `[McpServerTool]` method on it.
4. Maps the MCP endpoints at `/` via `app.MapMcp()` and a `/health` liveness probe.

Each tool call arrives over HTTP; the SDK dispatches to the static method, the method receives the shared `NpgsqlDataSource` as a DI-injected parameter, opens a pooled connection, runs the query, materializes the result as JSON, and returns the string. Exceptions are wrapped into a structured error JSON instead of propagating, so MCP clients always see a parseable response.

### Why a single shared `NpgsqlDataSource`

A `NpgsqlDataSource` owns the connection pool. Creating it once at startup lets every tool share connections and credentials, gives Npgsql the chance to multiplex prepared statements, and avoids the cost of opening a connection from scratch per call. `Pooling`, `MinPoolSize`, `MaxPoolSize`, `LoadBalanceHosts`, `Command Timeout` etc. from the connection string take effect across every tool invocation.

### Tool discovery and DI

`WithToolsFromAssembly()` scans the assembly for classes decorated with `[McpServerToolType]` and registers each `[McpServerTool(Name = "...")]` method. Method parameters whose types are registered in DI (`NpgsqlDataSource`, `CancellationToken`) are auto-injected by the runtime; the remaining parameters become the tool's public JSON schema inputs. Each parameter's `[Description]` attribute is propagated into the tool schema so the calling LLM sees rich, named arguments and default values.

## Project layout

```
psql_mcp/
├── PostgresMcpBridge.csproj    # net10.0, LangVersion 14.0
├── Program.cs                  # MCP + Npgsql wiring, host setup
├── Tools/
│   └── DatabaseTools.cs        # All ten MCP tools (~700 lines)
├── appsettings.json            # Logging + ConnectionStrings:Postgres
├── appsettings.Development.json
└── Properties/launchSettings.json
```

## Configuration

The only required setting is `ConnectionStrings:Postgres`. Standard Npgsql keys apply (`Host`, `Port`, `Database`, `Username`, `Password`, `SSL Mode`, `Pooling`, `Min/MaxPoolSize`, `LoadBalanceHosts`, `Command Timeout`, ...). Supply it via either:

- `appsettings.json`:
  ```json
  "ConnectionStrings": {
    "Postgres": "Host=...;Port=...;Database=...;Username=...;Password=...;"
  }
  ```
- Environment variable: `ConnectionStrings__Postgres` (double underscore).

The connection-string `Command Timeout` is used by Npgsql as the default per-command timeout. Most tools accept an explicit `timeoutSeconds` parameter that overrides it by setting `NpgsqlCommand.CommandTimeout` directly for that call (`0` means no timeout, default is `500`).

## Running

```
dotnet build
dotnet run
```

ASP.NET Core prints the listening URL (port from `Properties/launchSettings.json`). The host exposes:

| Path | Purpose |
|------|---------|
| `/` | MCP streamable HTTP endpoint (POST for tool calls, GET for SSE) |
| `/health` | Liveness probe, returns `{ "status": "ok" }` |

Point any MCP client that speaks streamable HTTP at the root URL.

## Tool reference

All tools live in `Tools/DatabaseTools.cs`. Every tool accepts an optional `timeoutSeconds` parameter (default `500`, `0` = no timeout) which is forwarded to `NpgsqlCommand.CommandTimeout`. All responses are JSON strings. On failure the response contains `success: false`, `error`, `errorType`; otherwise `success: true` plus the tool-specific payload.

### `execute_query(rawQuery, timeoutSeconds?)`

Runs an arbitrary SQL statement. For statements returning a result set, the response includes `rowCount`, `truncated`, `columns`, `rows` (a list of name→value dictionaries), and `timeTaken` (`Stopwatch.Elapsed.ToString()`, e.g. `"00:00:01.2345678"`). For non-row statements (`INSERT`/`UPDATE`/`DELETE` without `RETURNING`) it returns `rowsAffected` and `timeTaken`. A `MaxRows = 10_000` safety cap prevents accidentally streaming millions of rows; `truncated: true` signals when it kicked in.

### `get_indexes(tableName, timeoutSeconds?)`

Lists indexes on a table via `pg_index` / `pg_class` / `pg_am`, returning each index's name, full `pg_get_indexdef(...)` definition, uniqueness, primary-key flag, access method (`btree`, `hash`, `gin`, ...), and the ordered column list (reconstructed with `LATERAL unnest(indkey) WITH ORDINALITY` joined to `pg_attribute`). Table names may be schema-qualified (`public.users`).

### `check_query_speed(query, timeoutSeconds?)`

Executes `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) <query>` inside a transaction that is **always** rolled back, then extracts `Planning Time` and `Execution Time` from the JSON plan and reports both alongside a client-side `Stopwatch` round-trip time and the full plan tree. Because the transaction always rolls back, write statements measured this way never persist their effects.

### `list_tables(timeoutSeconds?)`

Returns every user table and partitioned table outside `pg_catalog` / `information_schema` with `schema_name`, `table_name`, `type`, `total_size` (via `pg_size_pretty(pg_total_relation_size(...))`), and `estimated_rows` (`reltuples::bigint`).

### `describe_schema(tableName, timeoutSeconds?)`

Two queries combined into one report:

- Column metadata from `pg_attribute`: `column_name`, `data_type` (via `format_type(atttypid, atttypmod)`), `is_nullable`, `default_value` (via `pg_get_expr(adbin, adrelid)`), and `ordinal_position`.
- Constraints from `pg_constraint`: name, kind (mapped from `contype` to `primary key` / `unique` / `check` / `exclusion` / `foreign key`), and `pg_get_constraintdef(oid)` text.

Schema-qualified names are supported.

### `check_bottleneck(timeoutSeconds?)`

A database-wide diagnostic that runs six independent read-only catalog queries on one connection and assembles them into a single report:

- `cacheHitRatio` — heap-block hit ratio from `pg_statio_user_tables`.
- `tablesWithHighSequentialScans` — top 10 by `seq_scan` (`pg_stat_user_tables`); potential missing-index targets.
- `tablesNeedingVacuum` — top 10 by `n_dead_tup` with computed dead-tuple ratio and `last_autovacuum`.
- `unusedIndexes` — `pg_stat_user_indexes` joined to `pg_index`, excluding primary/unique constraint indexes (which are kept for correctness even if unused).
- `longRunningQueries` — top 10 from `pg_stat_activity` where `state <> 'idle'` and runtime > 1 second, excluding the bridge's own session.
- `blockingLocks` — derived from `pg_blocking_pids(pid)`.

Nothing is modified.

### `explain_query(query, timeoutSeconds?)`

Runs `EXPLAIN (VERBOSE, FORMAT JSON) <query>` — plain `EXPLAIN`, no `ANALYZE`. The query is parsed and planned but **never executed**, so it is safe for expensive or destructive queries. Returns the top-level node type, the planner's estimated `Total Cost`, the estimated `Plan Rows`, and the full plan tree. Complements `check_query_speed`, which actually executes.

### `find_columns(namePattern, timeoutSeconds?)`

Searches `pg_attribute` across user tables, partitioned tables, views, and materialized views for columns whose name matches the pattern (`ILIKE`, case-insensitive). A bare word is wrapped as `%word%` for substring search; if the pattern already contains `%`, it is passed through verbatim. Returns each match's schema, relation name, object kind, column name, data type, and nullability.

### `list_functions(schema?, timeoutSeconds?)`

Inventories stored functions and procedures from `pg_proc` (excluding `pg_catalog` / `information_schema`), exposing name, argument list (`pg_get_function_arguments`), return type (`pg_get_function_result`), implementation language (`pg_language.lanname`), and kind (`prokind`: function / procedure / aggregate / window). Optionally restricted to a single schema.

### `get_function_definition(functionName, timeoutSeconds?)`

Returns the full `CREATE OR REPLACE FUNCTION/PROCEDURE` source via `pg_get_functiondef(oid)` for every overload matching the (optionally schema-qualified) name. Restricted to `prokind IN ('f', 'p')` so aggregates and window functions — which would raise inside `pg_get_functiondef` — are excluded.

## Implementation details

### Tool signature convention

Each tool is a `public static async Task<string>` method on `DatabaseTools`. The argument order is:

1. `NpgsqlDataSource dataSource` — injected from DI.
2. The tool's user-visible parameters, each annotated with `[Description("...")]`.
3. `int timeoutSeconds = DefaultTimeoutSeconds` — optional, propagates to `CommandTimeout`.
4. `CancellationToken cancellationToken = default` — the SDK populates this with the per-request token.

The MCP runtime exposes only parameters in positions 2 and 3 to the client; positions 1 and 4 are wiring it fills in.

### Result materialization

A private helper, `ReadRowsAsync(connection, sql, timeoutSeconds, ct, params (string, string?)[] parameters)`, builds an `NpgsqlCommand`, sets `CommandTimeout`, applies optional text parameters (typed as `NpgsqlDbType.Text` to avoid Npgsql's type inference choking on `IS NULL` comparisons against untyped DBNulls), runs `ExecuteReaderAsync`, and converts each row into a `Dictionary<string, object?>` keyed by column name. Values pass through `ConvertValue`, which maps `DBNull` to `null`, keeps JSON-friendly primitives as-is (numeric types, `bool`, `string`, `byte[]`, `DateTime`, `DateTimeOffset`, `Guid`), and falls back to `ToString()` for exotic CLR types so `System.Text.Json` can never throw on a row.

### Schema-qualified names

`SplitTableName("public.users")` returns `("public", "users")`; a bare `"users"` returns `(null, "users")`. The catalog queries use `(@schema IS NULL OR n.nspname = @schema)` so a missing schema means "any schema".

### Stopwatch / timing

`execute_query` starts a `Stopwatch` at the top of the method body and stops it just before serialization in both response branches, so the reported `timeTaken` covers connection acquisition, command execution and full row materialization. `check_query_speed` also wraps the EXPLAIN ANALYZE call to expose the client round-trip time alongside the server-side planning/execution times reported by Postgres.

### Safety

- `MaxRows = 10_000` caps the response from `execute_query` to avoid unbounded payloads; the `truncated` flag tells the caller this happened.
- `check_query_speed` runs inside a transaction that is always rolled back, so even DML measured via EXPLAIN ANALYZE leaves no trace.
- `execute_query` and `explain_query` accept raw SQL by design, since they are the bridge's purpose; every other tool parameterizes user input.
- `Include Error Detail=true` in the connection string surfaces the underlying Postgres error messages back to MCP clients via the `error` / `errorType` fields.

## Compatibility

The catalog queries target modern PostgreSQL features: `pg_proc.prokind` (PG 11+), `LATERAL ... WITH ORDINALITY` (PG 9.4+), `pg_blocking_pids` (PG 9.6+), `pg_statio_user_tables`, `pg_stat_user_tables`. Postgres-derived engines built on older kernels (e.g. some GaussDB / openGauss builds) may not implement every catalog function used here; in that case the affected tool returns a structured `{ success: false, error, errorType }` rather than crashing the server, and you can swap the SQL inside that tool for the dialect's equivalent.

## Build requirements

- .NET 10 SDK (this project targets `net10.0` with `LangVersion 14.0`).
- NuGet packages:
  - `ModelContextProtocol.AspNetCore` (1.3.0) — MCP server + streamable HTTP transport.
  - `Npgsql` (10.0.2) — PostgreSQL driver and `NpgsqlDataSource` pool.

  # License
  
  This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.
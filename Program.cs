using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Read the remote PostgreSQL connection string from configuration.
// Set it in appsettings.json (ConnectionStrings:Postgres) or via the
// environment variable ConnectionStrings__Postgres.
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "PostgreSQL connection string 'ConnectionStrings:Postgres' is not configured. " +
        "Set it in appsettings.json or via the environment variable ConnectionStrings__Postgres.");

// Register a single pooled Npgsql data source for the whole application.
// The MCP tools receive this via dependency injection.
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

// Register the MCP server, expose it over HTTP (streamable) transport,
// and auto-discover every [McpServerToolType] in this assembly.
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// MCP streamable-HTTP endpoint (handles POST/GET at the root path "/").
app.MapMcp();

// Simple liveness probe so you can confirm the host is up.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

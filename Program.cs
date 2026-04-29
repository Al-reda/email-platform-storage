using System.Text.Json.Serialization;
using Amazon;
using Amazon.DynamoDBv2;
using EmailPlatform.Storage.Configuration;
using EmailPlatform.Storage.Data;
using EmailPlatform.Storage.Endpoints;
using EmailPlatform.Storage.HealthChecks;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Formatting.Compact;

// ===== Factor 11 (Logs) =====================================================
// Configure Serilog as the bootstrap logger BEFORE the host is built so we
// capture any startup failures. Writes JSON lines to stdout — that's what
// Fargate/CloudWatch slurp up. We don't write to files. Ever.
// ============================================================================
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .Enrich.FromLogContext()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Replace default logger with Serilog.
    builder.Host.UseSerilog((ctx, sp, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console(new CompactJsonFormatter())
        .Enrich.FromLogContext());

    // ===== Factor 3 (Config) ================================================
    // Configuration flows from appsettings.json (defaults) + env vars (overrides).
    // Env var keys use double underscore as section separator, so
    // STORAGE__TABLENAME maps to Storage:TableName.
    // ========================================================================
    builder.Services.Configure<StorageOptions>(
        builder.Configuration.GetSection(StorageOptions.SectionName));

    // ===== Factor 4 (Backing services) ======================================
    // DynamoDB is treated as an attached resource. Its address is pure config;
    // swap endpoints (local vs AWS) without touching code.
    // ========================================================================
    builder.Services.AddSingleton<IAmazonDynamoDB>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
        var config = new AmazonDynamoDBConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(opts.Region)
        };
        if (!string.IsNullOrWhiteSpace(opts.ServiceUrl))
        {
            config.ServiceURL = opts.ServiceUrl;   // dev: point at DynamoDB Local
            config.AuthenticationRegion = opts.Region;
        }
        return new AmazonDynamoDBClient(config);
    });

    builder.Services.AddSingleton<IAnnouncementRepository, DynamoAnnouncementRepository>();

    // JSON: enums as strings (camelCase) both ways.
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.Converters.Add(
            new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    });

    // Health checks.
    builder.Services.AddSingleton<DynamoHealthCheck>();
    builder.Services.AddHealthChecks()
        .AddCheck<DynamoHealthCheck>("dynamodb");

    // OpenAPI / Swagger. Factor 13 (API First): the contract is documented.
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    // Swagger always on for this project — it's the API documentation
    // deliverable required by the assignment.
    app.UseSwagger();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/swagger/v1/swagger.json", "Storage Service v1");
        o.RoutePrefix = "swagger";
    });

    // Liveness: process is up.
    app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "storage" }));

    // Readiness: dependencies (DynamoDB) are reachable.
    app.MapHealthChecks("/health/ready");

    app.MapAnnouncementEndpoints();

    // ===== Factor 9 (Disposability) =========================================
    // On SIGTERM, ASP.NET Core triggers graceful shutdown automatically.
    // Kestrel stops accepting new connections, in-flight requests finish,
    // then the process exits. We don't need to write code for this, but we
    // do want to *log* that it's happening so the behaviour is visible.
    // ========================================================================
    var lifetime = app.Lifetime;
    lifetime.ApplicationStopping.Register(() =>
        Log.Information("Storage service received shutdown signal — draining connections..."));
    lifetime.ApplicationStopped.Register(() =>
        Log.Information("Storage service stopped cleanly."));

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Storage service failed to start");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

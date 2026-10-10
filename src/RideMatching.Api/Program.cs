using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc;
using RideMatching.Api.Hubs;
using RideMatching.Api.Infrastructure;
using RideMatching.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
        new BadRequestObjectResult(new ValidationProblemDetails(context.ModelState));
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() { Title = "Real-Time Ride Matching API", Version = "v1" });
});
builder.Services.AddSignalR().AddJsonProtocol(options =>
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();

var sqlRequested = builder.Configuration.GetValue("Database:UseSqlServer", true);
var connectionString = builder.Configuration.GetConnectionString("RideMatching");
var persistence = await SelectPersistenceAsync(sqlRequested, connectionString);
builder.Services.AddSingleton(persistence);
if (persistence.UsesSqlServer)
{
    builder.Services.AddSingleton<IDriverStore>(_ => new SqlDriverStore(connectionString!));
    builder.Services.AddSingleton<IRideStore>(_ => new SqlRideStore(connectionString!));
}
else
{
    builder.Services.AddSingleton<IDriverStore, InMemoryDriverStore>();
    builder.Services.AddSingleton<IRideStore, InMemoryRideStore>();
}
builder.Services.AddSingleton<IMatchQueue, MatchQueue>();
builder.Services.AddSingleton<IRideNotifier, SignalRRideNotifier>();
builder.Services.AddSingleton<MatchingService>();
builder.Services.AddSingleton<RideLifecycleService>();
builder.Services.AddHostedService<MatchingWorker>();

var app = builder.Build();

app.Logger.LogInformation("Persistence provider: {Provider}. {Detail}", persistence.Provider, persistence.Detail);

app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();
app.MapHub<RideHub>("/hubs/rides");
app.MapHealthChecks("/health");
app.MapGet("/api/system/storage", (PersistenceSelection selected) => Results.Ok(selected))
    .WithName("GetStorageProvider")
    .WithSummary("Shows whether SQL Server or the automatic in-memory fallback is active");
app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();

static async Task<PersistenceSelection> SelectPersistenceAsync(bool requested, string? connectionString)
{
    if (!requested)
        return new(false, "InMemory", "SQL Server is disabled by configuration.");
    if (string.IsNullOrWhiteSpace(connectionString))
        return new(false, "InMemory", "ConnectionStrings:RideMatching is missing.");

    try
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.Riders', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.Rides', N'U') IS NOT NULL THEN 1 ELSE 0 END;";
        var schemaReady = Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
        return schemaReady
            ? new(true, "SqlServer", "Connected successfully and the schema is ready.")
            : new(false, "InMemory", "SQL Server connected, but the required schema is missing. Run database/setup_and_seed.sql.");
    }
    catch (Exception exception) when (exception is SqlException or InvalidOperationException)
    {
        return new(false, "InMemory", $"SQL Server was unavailable at startup ({exception.GetType().Name}).");
    }
}

public partial class Program;

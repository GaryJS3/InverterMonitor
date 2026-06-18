using System.Text.Json;
using System.Text.Json.Serialization;
using InverterMonitor;

var builder = WebApplication.CreateBuilder(args);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.WriteIndented = true;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var initialSettings = builder.Configuration.GetSection("Monitor").Get<MonitorSettings>() ?? new MonitorSettings();
builder.Services.AddSingleton(new MonitorState(initialSettings));
builder.Services.AddSingleton<RegisterCatalog>();
builder.Services.AddSingleton<ModbusRtuOverTcpClient>();
builder.Services.AddSingleton<MqttPublisher>();
builder.Services.AddHostedService<InverterPollingService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/settings", (MonitorState state) => state.Settings);

app.MapPost("/api/settings", (MonitorState state, MonitorSettings settings) =>
{
    state.UpdateSettings(settings);
    return Results.Ok(state.Settings);
});

app.MapGet("/api/readings", (MonitorState state) => state.GetSnapshot());

app.MapGet("/api/inverter-definitions", (RegisterCatalog catalog) => catalog.Definitions);

app.MapGet("/api/registers", (RegisterCatalog catalog, MonitorState state) => catalog.ActiveDefinition(state.Settings).Registers);

app.MapGet("/api/events", async (HttpContext context, MonitorState state) =>
{
    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Content-Type", "text/event-stream");

    await foreach (var snapshot in state.WatchAsync(context.RequestAborted))
    {
        var json = JsonSerializer.Serialize(snapshot, jsonOptions);
        await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
});

app.Run();

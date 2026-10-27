using System.Globalization;
using Api;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection(ApiOptions.Section));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ApiOptions>>().Value);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LiveState>();
builder.Services.AddHostedService<StaticLoader>();
builder.Services.AddHostedService<VehicleFollower>();
builder.Services.AddHostedService<TripUpdatesFollower>();
builder.Services.AddHostedService<ModelRefresher>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");
api.MapGet("/health", (LiveState s) => s.Health());
api.MapGet("/vehicles", (LiveState s) => s.Snapshot());
api.MapGet("/vehicles/{id}", (string id, LiveState s) => s.Vehicle(id) is { } v ? Results.Ok(v) : Results.NotFound());
api.MapGet("/routes", (LiveState s) => s.Routes());
api.MapGet("/shapes/{id}", (string id, LiveState s, HttpContext ctx) =>
{
    if (s.Shape(id) is not { } shape) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "public, max-age=3600";
    return Results.Ok(shape);
});
// bbox=south,west,north,east
api.MapGet("/stops", (string bbox, LiveState s) =>
{
    var b = bbox.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
    return b.Length == 4 ? Results.Ok(s.StopsIn(b[0], b[1], b[2], b[3])) : Results.BadRequest("bbox=south,west,north,east");
});
api.MapGet("/stops/{id}", (string id, LiveState s) =>
    s.Stop(id) is { } stop ? Results.Ok(new { stop, departures = s.Departures(id) }) : Results.NotFound());

// Vehicle snapshots as server-sent events, one per VehiclePositions update (~10 s).
api.MapGet("/stream", async (HttpContext ctx, LiveState s, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    var channel = s.Subscribe();
    try
    {
        await foreach (var json in channel.Reader.ReadAllAsync(ct))
        {
            await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        s.Unsubscribe(channel);
    }
});

// Latest nightly (or ad hoc) backtest scores, overall and by mode.
api.MapGet("/scores", (ApiOptions options) =>
{
    if (!File.Exists(options.BacktestDbPath)) return Results.Ok(new { runs = Array.Empty<object>() });
    using var db = new SqliteConnection($"Data Source={options.BacktestDbPath};Mode=ReadOnly");
    db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = """
        SELECT run_at, test_from, test_to, predictor, grp, horizon_min, predicted, coverage, median_abs_s, bias_s, within_1m, early_rate
        FROM scores
        WHERE grp IN ('all', 'bus', 'rail') AND run_at IN (SELECT DISTINCT run_at FROM scores ORDER BY run_at DESC LIMIT 30)
        ORDER BY run_at, grp, horizon_min, predictor
        """;
    using var r = cmd.ExecuteReader();
    var rows = new List<object>();
    while (r.Read())
        rows.Add(new
        {
            runAt = r.GetString(0), testFrom = r.GetString(1), testTo = r.GetString(2), predictor = r.GetString(3), group = r.GetString(4),
            horizon = r.GetInt32(5), predicted = r.GetInt32(6), coverage = N(r, 7), medianAbs = N(r, 8), bias = N(r, 9), within1m = N(r, 10), early = N(r, 11),
        });
    return Results.Ok(new { runs = rows });

    static double? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
});

app.MapFallbackToFile("index.html");
app.Run();

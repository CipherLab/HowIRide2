using Ingest;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.Section));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<IngestOptions>>().Value);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ =>
{
    var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("transit-eta-ingest/0.1");
    return http;
});

var options = builder.Configuration.GetSection(IngestOptions.Section).Get<IngestOptions>() ?? new IngestOptions();
foreach (var feed in options.Realtime)
    builder.Services.AddSingleton<IHostedService>(sp => ActivatorUtilities.CreateInstance<RealtimePoller>(sp, feed));
builder.Services.AddHostedService<StaticGtfsWatcher>();

builder.Build().Run();

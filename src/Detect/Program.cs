using Detect;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<DetectOptions>(builder.Configuration.GetSection(DetectOptions.Section));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<DetectOptions>>().Value);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<DetectWorker>();
builder.Build().Run();

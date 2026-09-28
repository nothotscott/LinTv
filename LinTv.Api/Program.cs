using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using LinTv.Linux.Driver;
using LinTv.Core.Services;
using LinTv.Core.Configuration;
using LinTv.Core.Driver;
using LinTv.Core.Stores;
using LinTv.Core.Writers;
using LinTv.Core.Logging;
using LinTv.Core.Domain;

// Resolve appsettings.json next to the binary, not the caller's working directory,
// so `dotnet /opt/lintv/LinTv.Api.dll` works from anywhere.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
// Enums as names ("LiveView", not 2) in the JSON API. HDHomeRun endpoints use their own options.
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddRazorPages();

// Antiforgery and TempData encrypt with Data Protection keys. By default those land in the
// service user's $HOME/.aspnet (which resolved into /opt/lintv, breaking deploys) and are lost if
// that isn't writable. Keep them with the rest of the state so they survive deploys and restarts.
var storageDirectory = builder.Configuration.GetSection(LinTvConfiguration.SectionName)
    .Get<LinTvConfiguration>()?.StorageDirectory ?? new LinTvConfiguration().StorageDirectory;
builder.Services.AddDataProtection()
    .SetApplicationName("LinTv")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(storageDirectory, "keys")));
builder.Services.AddOpenApi();
builder.Services.AddOptions<LinTvConfiguration>()
    .BindConfiguration(LinTvConfiguration.SectionName)
    // Fail at startup on a typo like "25pm", rather than silently never scanning.
    .Validate(c => DailySchedule.TryParse(c.EpgScanTimes, out _, out _),
        "LinTv:EpgScanTimes must be times of day like \"11am\", \"11:30pm\" or \"23:00\"")
    .Validate(c => c.TunerCount >= 1, "LinTv:TunerCount must be at least 1")
    .ValidateOnStart();
builder.Services.AddSingleton<ITunerArbiterService, TunerArbiterService>();
builder.Services.AddSingleton<IDvbTuner, LinuxDvbTuner>((sp) =>
    new LinuxDvbTuner(
        sp.GetRequiredService<IOptions<LinTvConfiguration>>().Value.Adapter,
        sp.GetRequiredService<ILogger<LinuxDvbTuner>>()));
// dvr0 allows one reader: every consumer reads the multiplex through the broadcaster.
builder.Services.AddSingleton<IProgramStreamBroadcaster, ProgramStreamBroadcaster>();
builder.Services.AddSingleton<IChannelStore, JsonChannelStore>();
builder.Services.AddSingleton<IGuideStore, JsonGuideStore>();
builder.Services.AddSingleton<IChannelMapStore, JsonChannelMapStore>();
builder.Services.AddSingleton<IChannelScanner, ChannelScanner>();
builder.Services.AddSingleton<IM3uWriter, M3uWriter>();
builder.Services.AddSingleton<IEpgScanner, EpgScanner>();
builder.Services.AddSingleton<IXmlTvWriter, XmlTvWriter>();
builder.Services.AddSingleton<IHdHomeRunLineupService, HdHomeRunLineupService>();
builder.Services.AddSingleton<ISignalMeter, SignalMeter>();
builder.Services.AddHostedService<EpgScheduleService>();

// File log sink: registering the provider in DI makes the logging framework pick it up.
builder.Services.AddSingleton<FileLoggerProvider>();
builder.Services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<FileLoggerProvider>());
builder.Services.AddSingleton<ILogStore>(sp => sp.GetRequiredService<FileLoggerProvider>());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();
app.MapRazorPages();

app.Run();

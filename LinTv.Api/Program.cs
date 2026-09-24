using Microsoft.Extensions.Options;
using LinTv.Linux.Driver;
using LinTv.Core.Services;
using LinTv.Core.Configuration;
using LinTv.Core.Driver;
using LinTv.Core.Stores;
using LinTv.Core.Writers;

// Resolve appsettings.json next to the binary, not the caller's working directory,
// so `dotnet /opt/lintv/LinTv.Api.dll` works from anywhere.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddOptions<LinTvConfiguration>()
    .BindConfiguration(LinTvConfiguration.SectionName);
builder.Services.AddSingleton<ITunerArbiterService, TunerArbiterService>();
builder.Services.AddSingleton<IDvbTuner, LinuxDvbTuner>((sp) =>
    new LinuxDvbTuner(sp.GetRequiredService<IOptions<LinTvConfiguration>>().Value.Adapter));
builder.Services.AddSingleton<IChannelStore, JsonChannelStore>();
builder.Services.AddSingleton<IGuideStore, JsonGuideStore>();
builder.Services.AddSingleton<IChannelScanner, ChannelScanner>();
builder.Services.AddSingleton<IM3uWriter, M3uWriter>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();

app.Run();

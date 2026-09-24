using LinTv.Linux.Driver;
using LinTv.Core.Services;
using LinTv.Core.Configuration;
using LinTv.Core.Driver;
using LinTv.Core.Domain;
using LinTv.Core.Exceptions;
using Microsoft.Extensions.Options;

// Resolve appsettings.json next to the binary, not the caller's working directory,
// so `dotnet /opt/lintv/LinTv.Api.dll` works from anywhere.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddOptions<LinTvConfiguration>()
    .BindConfiguration(LinTvConfiguration.SectionName);
builder.Services.AddSingleton<ITunerArbiterService, TunerArbiterService>();
builder.Services.AddSingleton<IDvbTuner, LinuxDvbTuner>((sp) =>
    new LinuxDvbTuner(sp.GetRequiredService<IOptions<LinTvConfiguration>>().Value.Adapter));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// e.g. /test?frequencyHz=189000000 (US RF 9). ATSC tunes to the channel's center frequency.
app.MapGet("/test", async (long frequencyHz, IDvbTuner tuner, CancellationToken ct) =>
{
    await tuner.TuneAsync(frequencyHz, ct);
    return Results.Ok($"Tune request accepted for {frequencyHz} Hz");
});
// Whole RF multiplex as MPEG-TS; open in VLC via Media > Open Network Stream:
//   http://<host>:5249/stream?frequencyHz=189000000
// Pick a subchannel in VLC's Playback > Program menu.
app.MapGet("/stream", async (long frequencyHz, ITunerArbiterService tunerArbiter, HttpContext http, CancellationToken ct) =>
{
    IDvbTuner tuner;
    try
    {
        tuner = await tunerArbiter.AcquireAsync(frequencyHz, TunerPriority.LiveView, ct);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // ct is RequestAborted: closing VLC cancels the read loop and releases the demux.
    http.Response.ContentType = "video/mp2t";
    await foreach (var chunk in tuner.ReadTransportStreamAsync(ct))
    {
        await http.Response.Body.WriteAsync(chunk, ct);
    }

    await tunerArbiter.ReleaseAsync(ct);
    return Results.Empty;
});


app.Run();

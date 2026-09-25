using LinTv.Core.Logging;
using Microsoft.AspNetCore.Mvc;

namespace LinTv.Api.Controllers
{
    [ApiController]
    [Route("logs")]
    public class LogsController : ControllerBase
    {
        private const int MaxLines = 10_000;

        public ILogStore LogStore { private get; init; }

        public LogsController(ILogStore logStore)
        {
            LogStore = logStore;
        }

        /// Tail of a log file as plain text, e.g. /logs?lines=500 or /logs?file=lintv-20260925.log.
        [HttpGet]
        public async Task<IActionResult> Tail(string? file, int lines = 200, CancellationToken ct = default)
        {
            var tail = await LogStore.ReadTailAsync(file, Math.Clamp(lines, 1, MaxLines), ct);
            if (tail is null) return NotFound();
            return Content(string.Join('\n', tail) + "\n", "text/plain; charset=utf-8");
        }

        [HttpGet("files")]
        public IReadOnlyList<LogFileInfo> Files() => LogStore.ListFiles();
    }
}

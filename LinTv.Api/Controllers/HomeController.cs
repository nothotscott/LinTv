using Microsoft.AspNetCore.Mvc;

namespace LinTv.Api.Controllers
{
    /// Bare-bones test page for kicking off scans from a browser.
    [ApiController]
    public class HomeController : ControllerBase
    {
        private const string IndexHtml = """
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>LinTv</title>
              <style>
                body { font-family: system-ui, sans-serif; max-width: 40rem; margin: 2rem auto; padding: 0 1rem; }
                form { margin: 1rem 0; }
                button { padding: .4rem 1rem; }
              </style>
            </head>
            <body>
              <h1>LinTv</h1>

              <h2>Channel scan</h2>
              <form method="post" action="/scan/channels">
                <button type="submit">Start channel scan</button>
              </form>
              <p><a href="/scan/channels">Channel scan status</a></p>

              <h2>EPG scan</h2>
              <form method="post" action="/scan/epg">
                <button type="submit">Start EPG scan</button>
              </form>
              <p><a href="/scan/epg">EPG scan status</a></p>

              <h2>Lineup</h2>
              <p><a href="/lineup.m3u">lineup.m3u</a></p>
            </body>
            </html>
            """;

        [HttpGet("/")]
        public ContentResult Index() => Content(IndexHtml, "text/html; charset=utf-8");
    }
}

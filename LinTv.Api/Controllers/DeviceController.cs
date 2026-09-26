using LinTv.Core.Domain;
using LinTv.Core.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LinTv.Api.Controllers
{
    [ApiController]
    public class DeviceController : ControllerBase
    {
        // Identify as an ATSC 1.0 HDHomeRun CONNECT. Clients use ModelNumber/FirmwareName to pick
        // a feature set, so it has to be a real model string; TunerCount below is what they trust
        // for concurrency.
        private const string ModelNumber = "HDHR5-2US";
        private const string FirmwareName = "hdhomerun5_atsc";
        private const string FirmwareVersion = "20240101";

        // One HVR-1800, and TunerArbiterService arbitrates a single tuner.
        private const int TunerCount = 1;

        public LinTvConfiguration Config { private get; init; }

        public DeviceController(IOptions<LinTvConfiguration> config)
        {
            Config = config.Value;
        }

        [HttpGet("discover.json")]
        public IActionResult Discover()
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var discover = new HdHomeRunDiscover(
                FriendlyName: Config.FriendlyName,
                ModelNumber: ModelNumber,
                FirmwareName: FirmwareName,
                FirmwareVersion: FirmwareVersion,
                DeviceID: Config.DeviceId,
                DeviceAuth: "",
                BaseURL: baseUrl,
                LineupURL: $"{baseUrl}/lineup.json",
                TunerCount: TunerCount);
            return new JsonResult(discover, HdHomeRunJson.Options);
        }
    }
}

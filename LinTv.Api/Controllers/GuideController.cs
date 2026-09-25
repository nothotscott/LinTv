using LinTv.Core.Stores;
using LinTv.Core.Writers;
using Microsoft.AspNetCore.Mvc;

namespace LinTv.Api.Controllers
{
    [ApiController]
    public class GuideController : ControllerBase
    {
        public IChannelStore ChannelStore { private get; init; }

        public IGuideStore GuideStore { private get; init; }

        public IXmlTvWriter XmlTvWriter { private get; init; }

        public GuideController(
            IChannelStore channelStore,
            IGuideStore guideStore,
            IXmlTvWriter xmlTvWriter)
        {
            ChannelStore = channelStore;
            GuideStore = guideStore;
            XmlTvWriter = xmlTvWriter;
        }

        /// XMLTV guide for Plex/Jellyfin (see ChannelUrls.Guide).
        [HttpGet("guide.xml")]
        public async Task<IActionResult> XmlTv()
        {
            var channels = await ChannelStore.GetAllAsync();
            var events = await GuideStore.GetAllAsync();
            return Content(XmlTvWriter.Write(channels, events), "application/xml; charset=utf-8");
        }
    }
}

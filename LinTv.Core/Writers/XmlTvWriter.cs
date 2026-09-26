using LinTv.Core.Domain;
using System.Globalization;
using System.Text;
using System.Xml;

namespace LinTv.Core.Writers
{
    /// XMLTV (https://github.com/XMLTV/xmltv/blob/master/xmltv.dtd). Channel ids are the
    /// major.minor numbers, matching tvg-id in the M3U and GuideNumber in lineup.json.
    public class XmlTvWriter : IXmlTvWriter
    {
        public string Write(IReadOnlyList<VirtualChannel> channels, IReadOnlyList<GuideEvent> events,
            IReadOnlyList<ChannelMapping> mappings)
        {
            var extraNames = mappings
                .GroupBy(m => m.Channel)
                .ToDictionary(g => g.Key, g => g.SelectMany(m => m.DisplayNames).ToList());

            var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
            using var buffer = new MemoryStream();

            using (var xml = XmlWriter.Create(buffer, settings))
            {
                xml.WriteStartDocument();
                xml.WriteDocType("tv", null, "xmltv.dtd", null);
                xml.WriteStartElement("tv");
                xml.WriteAttributeString("generator-info-name", "LinTv");

                // Primaries only: an XMLTV channel id must be unique.
                var ordered = channels.Where(c => c.IsPrimary).OrderBy(c => c.Major).ThenBy(c => c.Minor).ToList();
                foreach (var c in ordered)
                {
                    xml.WriteStartElement("channel");
                    xml.WriteAttributeString("id", c.Id);
                    // Several display-names help clients auto-match by name or by number; mapped
                    // names (e.g. the network, "FOX") come last.
                    var names = ChannelNames(c, extraNames.GetValueOrDefault(c.Id))
                        .Select(Clean)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in names)
                    {
                        xml.WriteElementString("display-name", name);
                    }
                    xml.WriteEndElement();
                }

                var ids = ordered.Select(c => c.Id).ToHashSet();
                foreach (var e in events.Where(e => ids.Contains(e.ChannelId)).OrderBy(e => e.ChannelId).ThenBy(e => e.Start))
                {
                    xml.WriteStartElement("programme");
                    xml.WriteAttributeString("start", XmlTvTime(e.Start));
                    xml.WriteAttributeString("stop", XmlTvTime(e.Start + e.Duration));
                    xml.WriteAttributeString("channel", e.ChannelId);

                    xml.WriteStartElement("title");
                    xml.WriteAttributeString("lang", "en");
                    xml.WriteString(Clean(e.Title));
                    xml.WriteEndElement();

                    if (!string.IsNullOrWhiteSpace(e.Description))
                    {
                        xml.WriteStartElement("desc");
                        xml.WriteAttributeString("lang", "en");
                        xml.WriteString(Clean(e.Description));
                        xml.WriteEndElement();
                    }

                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
                xml.WriteEndDocument();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static IList<string> ChannelNames(VirtualChannel channel, IEnumerable<string>? extraNames = default)
        {
            var nameList = new List<string>();
            nameList.Add($"{channel.Id} {channel.ShortName}");
            nameList.Add(channel.ShortName);
            nameList.Add(channel.Id);
            if (extraNames is not null && extraNames.Any())
            {
                nameList.AddRange(extraNames);
            }

            return nameList;
        }

        private static string XmlTvTime(DateTimeOffset time) =>
            time.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";

        /// Broadcast text can contain control characters that XML forbids.
        private static string Clean(string text) =>
            string.Concat(text.Where(XmlConvert.IsXmlChar));
    }
}

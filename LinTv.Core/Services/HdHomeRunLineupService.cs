using LinTv.Core.Domain;
using LinTv.Core.Stores;
using LinTv.Core.Writers;
using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Services
{
    public class HdHomeRunLineupService : IHdHomeRunLineupService
    {
        public IChannelStore ChannelStore { private get; init; }

        public IChannelMapStore ChannelMapStore { private get; init; }

        public IChannelScanner ChannelScanner { private get; init; }

        public HdHomeRunLineupService(
            IChannelStore channelStore,
            IChannelMapStore channelMapStore,
            IChannelScanner channelScanner)
        {
            ChannelStore = channelStore;
            ChannelMapStore = channelMapStore;
            ChannelScanner = channelScanner;
        }

        public async Task<IEnumerable<HdHomeRunLineupEntry>> GetLineupAsync(string baseUrl)
        {
            var channels = await ChannelStore.GetAllAsync();
            var mappings = await ChannelMapStore.GetAllAsync();

            var extraNames = mappings
                .GroupBy(m => m.Channel)
                .ToDictionary(g => g.Key, g => g.SelectMany(m => m.DisplayNames).ToList());

            var lineup = new List<HdHomeRunLineupEntry>();
            foreach (var channel in channels)
            {
                if (!channel.IsPrimary)
                    continue;
                var name = extraNames.GetValueOrDefault(channel.Id)?.FirstOrDefault() ?? channel.ShortName;
                lineup.Add(new HdHomeRunLineupEntry(channel.Id, name, ChannelUrls.Stream(baseUrl, channel)));
            }

            return lineup;
        }

        public async Task<HdHomeRunLineupStatus> LineupStatusAsync()
        {
            var scan = ChannelScanner.Status;
            var status = scan.InProgress
                ? new HdHomeRunLineupStatus(1, Progress: scan.ProgressPercent, Found: scan.Found)
                : new HdHomeRunLineupStatus(0, ScanPossible: 1, Source: "Antenna", SourceList: ["Antenna"]);
            return status;
        }
    }
}

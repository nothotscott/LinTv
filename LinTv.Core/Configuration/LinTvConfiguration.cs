using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Configuration
{
    public class LinTvConfiguration
    {
        public const string SectionName = "LinTv";

        /// Persistent state (channels.json, guide.json). /var/lib/<app> is the FHS home for service state.
        public string StorageDirectory { get; set; } = "/var/lib/lintv";

        /// Index N of /dev/dvb/adapterN.
        public int Adapter { get; set; }

        public int LockWaitSeconds { get; set; }

        public int ChannelScanTimeoutSeconds { get; set; }

        /// How long to collect guide tables per multiplex before settling for what arrived.
        /// Far-future EITs and ETTs repeat slowly (up to a minute or more).
        public int EpgScanTimeoutSeconds { get; set; } = 60;

        /// Daily log files in {StorageDirectory}/logs older than this are deleted.
        public int LogRetentionDays { get; set; } = 7;

        /// Name Plex/Jellyfin show for the tuner.
        public string FriendlyName { get; set; } = "LinTv";

        /// HDHomeRun device ID: 8 hex digits. Clients key the tuner (and its guide mapping) on
        /// this, so keep it stable -- changing it makes Plex/Jellyfin see a new device.
        /// The default is "LinT" in ASCII.
        public string DeviceId { get; set; } = "4C696E54";
    }
}

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
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace LinTv.Core.Configuration
{
    public class LinTvConfiguration
    {
        public const string SectionName = "LinTv";

        /// Index N of /dev/dvb/adapterN.
        public int Adapter { get; set; }

        public int LockWaitSeconds { get; set; }
    }
}

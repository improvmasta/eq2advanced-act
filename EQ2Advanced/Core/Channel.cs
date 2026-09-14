namespace EQ2Advanced.Core
{
    /// <summary>
    /// Which BUILD this is.
    ///
    /// One source tree produces two DLLs: the stable uploader, and a TEST TRACK
    /// that adds multi-log support. The test build is a straight REPLACEMENT —
    /// same assembly name, same ACT tab, same config file, same cursors — so
    /// installing it means dropping `EQ2Advanced.dll` over the old one, and
    /// going back means dropping the stable one back. There is never a second
    /// plugin in the list, and never a second thing to pair.
    ///
    /// What forks here is therefore small on purpose: the version it reports,
    /// and the product token it reports it under, so the site can tell which
    /// build a raider is on without ever offering them the other as an
    /// "update". Behaviour forks on <see cref="MultiLog"/> at the three places
    /// that actually differ, so the stable build's code path stays the code
    /// path it has always been rather than a special case of a newer one.
    /// </summary>
    internal static class Channel
    {
#if MULTILOG
        public const bool MultiLog = true;
        public const string Version = "0.3.3";
        public const string UserAgentProduct = "eq2advanced-act-multi";
        public const string Title = "eq2advanced uploader - multi-log test build";
#else
        public const bool MultiLog = false;
        public const string Version = EQ2AdvancedPlugin.StableVersion;
        public const string UserAgentProduct = "eq2advanced-act";
        public const string Title = "eq2advanced uploader";
#endif

        /// <summary>The same in both builds, and that is the point: one tab,
        /// one config file, one set of cursors. A replacement inherits the
        /// pairing and the acknowledged offsets of the build it replaces, so
        /// swapping the DLL mid-week costs nothing and loses no log.</summary>
        public const string TabName = "eq2advanced";
        public const string ConfigFileName = "EQ2Advanced.json";
    }
}

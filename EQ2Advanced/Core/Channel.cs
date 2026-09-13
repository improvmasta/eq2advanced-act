namespace EQ2Advanced.Core
{
    /// <summary>
    /// Which BUILD this is.
    ///
    /// One source tree produces two DLLs: the stable uploader everybody runs,
    /// and a TEST TRACK that adds multi-log support. They are separate
    /// assemblies (`EQ2Advanced.dll` / `EQ2AdvancedMulti.dll`) rather than one
    /// assembly with a switch, for a reason that is not cosmetic: ACT loads
    /// every plugin into ONE AppDomain, so two files claiming the same assembly
    /// identity cannot both be listed — and the whole point of a test track is
    /// that installing it leaves the working uploader on disk, enabled or not,
    /// one checkbox away.
    ///
    /// What forks here is IDENTITY, never behaviour. Behaviour forks on
    /// <see cref="MultiLog"/> at the three places that actually differ, so the
    /// stable build's code path stays the code path it has always been rather
    /// than a special case of a newer one.
    ///
    /// Two of these are load-bearing:
    ///
    /// * <b>The config file is per channel.</b> The test build must not write
    ///   its half-finished settings into the config the stable build reads.
    ///
    /// * <b>The CURSOR directory is NOT.</b> See
    ///   <see cref="JsonStore.CursorDirectory"/> — both builds share it, and
    ///   they have to.
    /// </summary>
    internal static class Channel
    {
#if MULTILOG
        public const bool MultiLog = true;
        public const string Version = "0.3.0";
        public const string TabName = "eq2advanced (multi-log)";
        public const string ConfigFileName = "EQ2Advanced.Multi.json";
        public const string UserAgentProduct = "eq2advanced-act-multi";
        public const string Title = "eq2advanced uploader — multi-log test build";
#else
        public const bool MultiLog = false;
        public const string Version = EQ2AdvancedPlugin.StableVersion;
        public const string TabName = "eq2advanced";
        public const string ConfigFileName = "EQ2Advanced.json";
        public const string UserAgentProduct = "eq2advanced-act";
        public const string Title = "eq2advanced uploader";
#endif

        /// <summary>The stable build's config, read ONCE to seed a fresh test
        /// track config (see <see cref="Settings.Load"/>). Pairing again on a
        /// second tab to upload to the same account is busywork, and busywork
        /// at the pairing step is where people give up on a test build.</summary>
        public const string StableConfigFileName = "EQ2Advanced.json";
    }
}

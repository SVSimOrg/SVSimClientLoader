namespace SVSimLoader;

public static class SvSimConfig
{
    public static string ApplicationUrl { get; set; }
    public static string ResourceUrl { get; set; }
    public static bool DisableEncryption { get; set; }
    public static bool DumpCardDB { get; set; }
    public static bool EnableTrafficCapture { get; set; }
    public static bool EnableBattleCapture { get; set; }
    public static bool EnableSpinProbe { get; set; }
    public static bool DumpUserData { get; set; }
    public static bool SweepGachaExchange { get; set; }
    public static float GachaExchangeSweepPacingSeconds { get; set; }
    public static string SweepDryRunIds { get; set; }
    public static bool SweepMainStory { get; set; }
    public static bool SweepLimitedStory { get; set; }
    public static bool SweepEventStory { get; set; }
    public static float StorySweepPacingSeconds { get; set; }
    public static bool ProbeLimitedSection { get; set; }
    public static bool ProbeEventSection { get; set; }
    public static string StorySectionIdFilter { get; set; }
    public static bool NukeIdentityOnStartup { get; set; }
    public static bool ClearAssetCacheOnStartup { get; set; }

    // Multi-instance (same-machine two-client PvP smoke) — driven by the SVSIM_INSTANCE_ID env var.
    public static bool SecondaryInstance { get; set; }   // true when SVSIM_INSTANCE_ID is set
    public static int InstanceId { get; set; }            // parsed env value
    public static string IdentityFilePath { get; set; }   // per-instance identity file
    public static ulong FakeSteamId { get; set; }         // 900000 + InstanceId
    public static string FakeTicket { get; set; }         // even-length hex of FakeSteamId
}

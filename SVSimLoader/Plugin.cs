using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SVSimLoader.Patches;

namespace SVSimLoader
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        public static Plugin Instance { get; private set; }
        private ConfigEntry<string> _applicationUrl;
        private ConfigEntry<string> _resourceUrl;
        private ConfigEntry<bool> _disableEncryption;
        private void Awake()
        {
            Instance = this;
            Log = base.Logger;
            var instanceRaw = System.Environment.GetEnvironmentVariable("SVSIM_INSTANCE_ID");
            if (!string.IsNullOrEmpty(instanceRaw) && int.TryParse(instanceRaw, out var instanceId) && instanceId > 0)
            {
                SvSimConfig.SecondaryInstance = true;
                SvSimConfig.InstanceId = instanceId;
                SvSimConfig.FakeSteamId = 900000UL + (ulong)instanceId;
                // Ticket must be non-empty, even-length, valid hex (the server HexDecodes it before
                // the bypass) and DISTINCT per instance (SteamSessionService caches ticket->steamId
                // and rejects a reused ticket under a different steamId).
                string hex = SvSimConfig.FakeSteamId.ToString("x");
                if (hex.Length % 2 == 1) hex = "0" + hex;
                SvSimConfig.FakeTicket = hex;
                SvSimConfig.IdentityFilePath =
                    System.IO.Path.Combine(Paths.ConfigPath, $"svsim-identity-{instanceId}.json");
                InstanceIdentityStore.Initialize(SvSimConfig.IdentityFilePath);
                Logger.LogWarning(
                    $"MULTI-INSTANCE MODE: id={instanceId}, fakeSteamId={SvSimConfig.FakeSteamId}, " +
                    $"identity file={SvSimConfig.IdentityFilePath}. Single-instance mutex will be skipped.");
            }
            // Plugin startup logic
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
            _applicationUrl = Config.Bind("Connection", "ApplicationUrl", "https://utoongaize.shadowverse.jp/shadowverse/",
                "The URL to the application server.");
            _resourceUrl = Config.Bind("Connection", "ResourceUrl", "https://shadowverse.akamaized.net/",
                "The URL to the asset CDN (resource server #3). Must end with a trailing slash. " +
                "Default points at the prod Akamai CDN — change to e.g. http://localhost:5149/ to redirect " +
                "to a local SVSim.ContentServer populated by data_dumps/scripts/content_cdn_mirror.py.");
            _disableEncryption = Config.Bind("Connection", "DisableEncryption", false,
                "Whether to disable encrypting HTTP requests");
            SvSimConfig.EnableTrafficCapture =
                Config.Bind("Capture", "EnableTrafficCapture", true,
                    "Write each HTTP request/response body to traffic.ndjson in the current capture session directory").Value;
            SvSimConfig.EnableBattleCapture =
                Config.Bind("Capture", "EnableBattleCapture", true,
                    "Write each Socket.IO battle send/receive body to battle-traffic.ndjson in the current capture session directory").Value;
            SvSimConfig.EnableSpinProbe =
                Config.Bind("Capture", "EnableSpinProbe", false,
                    "Diagnostic for the battle-node `spin` investigation. On every received battle frame (OperateReceive.StartOperate) and every emitted frame (NetworkBattleSender.EmitMsg), append {event, uri, spin, count} to spin-rng.ndjson, where `count` is BattleManagerBase.stableRandomCount (the cumulative shared-RNG draw tally, read before the spin crank). Lets us compare per-turn local draw deltas against the inbound spin to settle whether prod authors spin from server-side simulation or it is wire-derivable. Run against PROD (servers live until end of June 2026) to capture real spin values. See docs/audits/battle-node-spin-rng-model-2026-06-04.md.").Value;
            SvSimConfig.DumpCardDB =
                Config.Bind("Capture", "DumpCardDB", false,
                    "Dumps the loaded card master to cards.json in the current capture session directory").Value;
            SvSimConfig.DumpUserData =
                Config.Bind("Capture", "DumpUserData", false,
                    "On every /load/index response, extract essential viewer fields into user-data.json (suitable for POSTing to /admin/import_viewer on the local server)").Value;
            SvSimConfig.ProbeLimitedSection =
                Config.Bind("Sweeps", "ProbeLimitedSection", false,
                    "On the first /mypage/refresh response of the session, fire /limited_story/section to discover whether limited-story content is currently scheduled for this account. Response captured to traffic.ndjson. If SweepLimitedStory is also enabled, the probe's response triggers the sweep automatically.").Value;
            SvSimConfig.ProbeEventSection =
                Config.Bind("Sweeps", "ProbeEventSection", false,
                    "As ProbeLimitedSection but for /event_story/section. Fired with a 1s gap after the limited probe (if both are enabled).").Value;
            SvSimConfig.SweepGachaExchange =
                Config.Bind("Sweeps", "SweepGachaExchange", false,
                    "On the first /pack/info response of the session, fire /pack/get_gacha_point_rewards (GachaPointExchangeInfoTask) for every pack id in Wizard.Data.Master.CardSetNameMgr.GetList() (~279 ids), minus ids already known to fail from the persistent miss ledger at BepInEx/svsim-captures/gacha-sweep-misses.json. Replaces SweepLeaderSkinPools (which only covered the 35 in-catalog packs from /pack/info's pack_config_list). Responses captured into traffic.ndjson via the existing traffic hook. One-shot per session. WARNING: hits prod API — at 0.5s/request default, full sweep is ~2–5min wall-clock.").Value;
            SvSimConfig.GachaExchangeSweepPacingSeconds =
                Config.Bind("Sweeps", "GachaExchangeSweepPacingSeconds", 0.5f,
                    "Seconds to wait between consecutive /pack/get_gacha_point_rewards requests during the gacha-exchange sweep. Clamped to a minimum of 0.1s. Default 0.5s mirrors the old LeaderSkinPoolSweep pacing.").Value;
            SvSimConfig.SweepDryRunIds =
                Config.Bind("Sweeps", "SweepDryRunIds", "",
                    "Optional comma-separated list of pack ids to restrict the gacha-exchange sweep to (e.g. '80008,97002,93025'). Empty = full sweep. Use this to smoke-test the sweep on a handful of known-ambiguous packs before committing the session to a full ~5min run.").Value;
            SvSimConfig.SweepMainStory =
                Config.Bind("Sweeps", "SweepMainStory", false,
                    "On the first story-section response of the session, walk every (section, chara, chapter) in Data.StoryWorldDataManager matching StoryApiType.MainStory and emit /main_story/start + /main_story/finish (no-battle skip shape) per chapter with is_skip_enabled. Captures master `special_battle_setting` payloads (server-only data) into traffic.ndjson via the existing hook. Per-family one-shot per session. SIDE EFFECT: unfinished chapters become is_skipped=true is_finish=false (blue 'Cleared' in UI, no rewards). Use a throwaway account. WARNING: hits prod API — at 5s pacing, ~6h for the full main-story tree.").Value;
            SvSimConfig.SweepLimitedStory =
                Config.Bind("Sweeps", "SweepLimitedStory", false,
                    "As SweepMainStory but for StoryApiType.LimitedStory sections. Requires the loaded world data to include limited-story sections (navigate to the Limited Story tab in-game first if /story/section didn't surface them).").Value;
            SvSimConfig.SweepEventStory =
                Config.Bind("Sweeps", "SweepEventStory", false,
                    "As SweepMainStory but for StoryApiType.EventStory sections. Requires the loaded world data to include event-story sections (navigate to the Event Story tab in-game first if /story/section didn't surface them).").Value;
            SvSimConfig.StorySweepPacingSeconds =
                Config.Bind("Sweeps", "StorySweepPacingSeconds", 5.0f,
                    "Seconds to wait between consecutive requests during a story sweep. Clamped to a minimum of 1s. Default 5s is conservative for prod-API politeness. Lower values speed up the sweep but increase rate-limit/anti-cheat exposure.").Value;
            SvSimConfig.StorySectionIdFilter =
                Config.Bind("Sweeps", "StorySectionIdFilter", "",
                    "Optional comma-separated list of section IDs to restrict the story sweep to (e.g. '14,19,20'). Empty = sweep all sections. Useful for resuming a previous run that hit MAX_PASSES_PER_PAIR on specific sections without re-sweeping everything.").Value;
            SvSimConfig.NukeIdentityOnStartup =
                Config.Bind("Identity", "NukeIdentityOnStartup", false,
                    "On plugin Awake (before the game reads PlayerPrefs), wipe all PlayerPrefs via PlayerPrefs.DeleteAll(). Clears the obscured UDID/VIEWER_ID/SHORT_UDID keys that Cute.Certification reads on login, so the next launch behaves like a brand-new install and re-runs SignUpTask. Use this when switching Steam accounts gives a linking error. SIDE EFFECT: also resets language/sound/RES_VER prefs — they're rebuilt from defaults next boot. Recovery files and capture sessions are NOT touched.").Value;
            SvSimConfig.ClearAssetCacheOnStartup =
                Config.Bind("Maintenance", "ClearAssetCacheOnStartup", false,
                    "On plugin Awake (before the game initializes the asset system), delete the locally-cached " +
                    "CDN asset state under <persistentDataPath>: manifest.db (install-state KV) + the manifest/, " +
                    "a/, b/, s/, v/, m/, f/ subtrees. Next boot triggers the same 'X MB to download' prompt a " +
                    "fresh install sees, and refetches everything against whatever ResourceUrl is configured. " +
                    "Useful for testing the local content server, validating manifest changes, or forcing a refresh " +
                    "after switching between local + prod CDNs. SIDE EFFECT: discards all downloaded asset bundles " +
                    "(can be many GB). Does NOT touch PlayerPrefs (incl. RES_VER), identity keys, cardmaster/, " +
                    "Cookies/, NewReplay/, recovery/, or any *_log/ dirs. Safe to combine with NukeIdentityOnStartup " +
                    "for a full 'brand-new install' state.").Value;
            SvSimConfig.ApplicationUrl = _applicationUrl.Value;
            SvSimConfig.ResourceUrl = _resourceUrl.Value;
            SvSimConfig.DisableEncryption = _disableEncryption.Value;
            if (SvSimConfig.NukeIdentityOnStartup)
            {
                Logger.LogWarning("NukeIdentityOnStartup is enabled — wiping all PlayerPrefs (identity + settings).");
                IdentityWipe.Execute();
            }
            if (SvSimConfig.ClearAssetCacheOnStartup)
            {
                Logger.LogWarning("ClearAssetCacheOnStartup is enabled — wiping local asset cache (manifest.db + a/b/s/v/m/f/manifest/).");
                AssetCacheWipe.Execute(Logger);
            }
            CaptureWriter.Initialize();
            Logger.LogInfo($"Capture session directory: {CaptureWriter.SessionDirectory}");
            Logger.LogInfo($"Connecting to application server at {_applicationUrl.Value}");
            Logger.LogInfo($"Fetching assets from resource server at {_resourceUrl.Value}");
            ExceptionLogging.Install();
            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Logger.LogInfo("Patched");
        }
    }
}
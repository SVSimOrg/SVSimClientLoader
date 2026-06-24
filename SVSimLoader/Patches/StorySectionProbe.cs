using System.Collections;
using Cute;
using UnityEngine;
using Wizard;
using Wizard.Story;

namespace SVSimLoader.Patches;

/// <summary>
/// On the first /mypage/refresh response of the session, fires /limited_story/section
/// and/or /event_story/section probes (per per-family config flags) to discover whether
/// limited or event content is currently scheduled / unlocked for this account.
///
/// Responses are captured by the existing ExaminationPatches.SetResponseData hook into
/// traffic.ndjson. If the matching SweepLimitedStory / SweepEventStory flag is ALSO
/// enabled, the probe's response will trigger that sweep via StorySweep.OnSectionResponse —
/// so probe + sweep combined is a single-config flow.
///
/// Triggered on /mypage/refresh (home-screen confirmation) rather than the section URLs
/// themselves to avoid stomping Data.StoryWorldDataManager mid-navigation while the user
/// is on a story screen. On the home page, no other code reads WorldDataManager.
/// </summary>
internal static class StorySectionProbe
{
    private static bool _probed;
    private static readonly object _lock = new object();

    public static void OnMypageRefreshResponse()
    {
        bool runLimited, runEvent;
        lock (_lock)
        {
            if (_probed) return;
            runLimited = SvSimConfig.ProbeLimitedSection;
            runEvent = SvSimConfig.ProbeEventSection;
            if (!runLimited && !runEvent) return;
            _probed = true;
        }
        if (Plugin.Instance == null)
        {
            Plugin.Log.LogError("StorySectionProbe: Plugin.Instance is null — cannot start coroutine.");
            return;
        }

        Plugin.Log.LogInfo($"StorySectionProbe: triggered (limited={runLimited}, event={runEvent}).");
        Plugin.Instance.StartCoroutine(ProbeCoroutine(runLimited, runEvent));
    }

    private static IEnumerator ProbeCoroutine(bool runLimited, bool runEvent)
    {
        if (runLimited)
        {
            yield return Probe(StoryEntranceType.LimitedStory, "limited");
            yield return new WaitForSeconds(1f);
        }
        if (runEvent)
        {
            yield return Probe(StoryEntranceType.EventStory, "event");
        }
    }

    private static IEnumerator Probe(StoryEntranceType entrance, string label)
    {
        var info = new SelectedStoryInfo(entrance);
        var task = new StorySectionTask(info);
        task.SetParameter(false);
        yield return Toolbox.NetworkManager.Connect(task, _ => { });
        if (task.isServerResultCodeOK())
        {
            Plugin.Log.LogInfo($"StorySectionProbe[{label}]: ok (response in traffic.ndjson).");
        }
        else
        {
            Plugin.Log.LogWarning($"StorySectionProbe[{label}]: rc={task.GetResultCode()}.");
        }
    }
}

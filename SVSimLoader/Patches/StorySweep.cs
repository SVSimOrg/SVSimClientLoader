using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Cute;
using UnityEngine;
using Wizard;
using Wizard.Story;

namespace SVSimLoader.Patches;

/// <summary>
/// On the first /{story,main_story,limited_story,event_story}/section response of the
/// session (for whichever families are enabled in config), walks every (section, chara,
/// chapter) and emits /start + /finish (no-battle skip shape) per chapter. This captures
/// the master `special_battle_setting` payload — server-only data, unrecoverable post-shutdown
/// per project-story-capture-cheat memory — without needing recovery_data or any played battle.
///
/// Triggered automatically when the user lands on a story screen. Per-family one-shot per
/// session, gated by SvSimConfig.Sweep{Main,Limited,Event}Story flags (default OFF).
///
/// /start and /finish responses are captured automatically by the existing traffic hook in
/// ExaminationPatches.SetResponseData — no extra writer needed.
///
/// SIDE EFFECTS on the active account:
///   - Unfinished chapters become is_skipped=true is_finish=false (blue "Cleared" in UI,
///     no rewards granted). Already-cleared chapters are unchanged.
///   - Hits prod API at the configured pacing (default 5s/request).
///   - 2 calls per chapter; main_story alone is ~6h at 5s pacing for ~2400 chapters.
/// Use a throwaway / capture-purpose account.
///
/// Iteration order mimics natural play progression: sections sorted by AllStoryOrderId ASC
/// (oldest world first), charas in leader_list order (chara_id 1..8), chapters by
/// ChapterRowNum ASC with chapter_id as tiebreaker (handles "12a" / "12b" branching suffixes).
/// </summary>
internal static class StorySweep
{
    private static bool _mainStarted;
    private static bool _limitedStarted;
    private static bool _eventStarted;
    private static readonly object _lock = new object();

    /// <summary>
    /// Called from ExaminationPatches.SetResponseData when ANY of the four story-section URLs
    /// lands. Schedules one coroutine per (enabled, not-yet-started) family.
    /// </summary>
    private struct Family
    {
        public StoryApiType ApiType;
        public string Label;
    }

    public static void OnSectionResponse(string url)
    {
        var toRun = new List<Family>();
        lock (_lock)
        {
            if (SvSimConfig.SweepMainStory && !_mainStarted)
            {
                _mainStarted = true;
                toRun.Add(new Family { ApiType = StoryApiType.MainStory, Label = "main" });
            }
            if (SvSimConfig.SweepLimitedStory && !_limitedStarted)
            {
                _limitedStarted = true;
                toRun.Add(new Family { ApiType = StoryApiType.LimitedStory, Label = "limited" });
            }
            if (SvSimConfig.SweepEventStory && !_eventStarted)
            {
                _eventStarted = true;
                toRun.Add(new Family { ApiType = StoryApiType.EventStory, Label = "event" });
            }
        }
        if (toRun.Count == 0) return;
        if (Plugin.Instance == null)
        {
            Plugin.Log.LogError("StorySweep: Plugin.Instance is null — cannot start coroutine.");
            return;
        }

        foreach (var entry in toRun)
        {
            Plugin.Log.LogInfo($"StorySweep[{entry.Label}]: triggered by {url}.");
            Plugin.Instance.StartCoroutine(SweepCoroutine(entry.ApiType, entry.Label));
        }
    }

    private static IEnumerator SweepCoroutine(StoryApiType apiType, string label)
    {
        // Defer one frame so the triggering task's Parse() has time to populate
        // Data.StoryWorldDataManager (Parse runs after our SetResponseData prefix returns).
        yield return null;

        float pace = Mathf.Max(1f, SvSimConfig.StorySweepPacingSeconds);
        var entrance = ApiToEntrance(apiType);

        var allSections = Data.StoryWorldDataManager?.SectionDatas;
        if (allSections == null || allSections.Count == 0)
        {
            Plugin.Log.LogWarning(
                $"StorySweep[{label}]: Data.StoryWorldDataManager.SectionDatas was empty after Parse() — aborted.");
            yield break;
        }

        // Walk in natural chronological order: oldest world/section first.
        var sections = allSections
            .Where(s => s != null && s.StoryApiType == apiType && !s.IsUnderMaintenance)
            .OrderBy(s => s.AllStoryOrderId)
            .ToList();

        // Optional section-id filter — used to resume a sweep that capped on specific
        // (section, chara) pairs without re-walking the whole story tree.
        var filterRaw = SvSimConfig.StorySectionIdFilter;
        if (!string.IsNullOrEmpty(filterRaw))
        {
            var allowedIds = new HashSet<int>();
            foreach (var part in filterRaw.Split(','))
            {
                int id;
                if (int.TryParse(part.Trim(), out id)) allowedIds.Add(id);
            }
            if (allowedIds.Count > 0)
            {
                var before = sections.Count;
                sections = sections.Where(s => allowedIds.Contains(s.Id)).ToList();
                Plugin.Log.LogInfo(
                    $"StorySweep[{label}]: StorySectionIdFilter='{filterRaw}' active — {sections.Count}/{before} sections retained.");
            }
        }

        if (sections.Count == 0)
        {
            Plugin.Log.LogInfo(
                $"StorySweep[{label}]: no sections matched StoryApiType.{apiType} in the loaded world data. " +
                "Navigate to that story tab in-game (or trigger /{family}/section directly) to seed it.");
            yield break;
        }

        Plugin.Log.LogInfo(
            $"StorySweep[{label}]: {sections.Count} sections queued. Pacing={pace}s between requests.");

        int chapters = 0, startOk = 0, startFail = 0, finishOk = 0, finishFail = 0;
        int tutorialPlayOk = 0, tutorialPlayFail = 0;
        for (int si = 0; si < sections.Count; si++)
        {
            var section = sections[si];
            var info = new SelectedStoryInfo(entrance);
            info.SetSection(section);

            List<int?> charaIds;
            var currentChapterByChara = new Dictionary<int, int>();
            if (section.IsLeaderSelect)
            {
                var leaderTask = new StoryLeaderSelectTask(info);
                yield return Toolbox.NetworkManager.Connect(leaderTask, _ => { });
                yield return new WaitForSeconds(pace);
                if (!leaderTask.isServerResultCodeOK())
                {
                    Plugin.Log.LogWarning(
                        $"StorySweep[{label}]: section {section.Id} /leader_select rc={leaderTask.GetResultCode()}; skipping section.");
                    continue;
                }
                var leaderList = Data.StoryLeaderSelect?.DataList ?? new List<StoryLeaderSelectData>();
                charaIds = leaderList.Select(d => (int?)d.CharaId).ToList();
                foreach (var d in leaderList)
                {
                    if (int.TryParse(d.CurrentChapter, out int cur))
                    {
                        currentChapterByChara[d.CharaId] = cur;
                    }
                }
                if (charaIds.Count == 0)
                {
                    Plugin.Log.LogWarning(
                        $"StorySweep[{label}]: section {section.Id} leader_list was empty; skipping section.");
                    continue;
                }
            }
            else
            {
                charaIds = new List<int?> { null };
            }

            for (int ci = 0; ci < charaIds.Count; ci++)
            {
                var charaId = charaIds[ci];
                info.SetSectionChara(charaId);

                // Multi-pass loop: server unlocks chapter N+1 only after chapter N is cleared,
                // so /info's chapter list grows during the sweep. Re-fetch after each pass and
                // process any newly-released chapters until /info stabilizes (or MAX_PASSES).
                // Without this, only the first wave of is_released chapters per pair gets
                // processed and ~14 follow-on chapters per chara go missed.
                var processedStoryIds = new HashSet<int>();
                // The processedStoryIds HashSet guarantees we never retry the same chapter,
                // so the natural bound is the actual chapter count per (section, chara) —
                // realistically <40 even for the longest late-game arcs. 50 is a generous
                // safety net for a runaway loop while comfortably covering all real sections.
                // (Earlier runs with cap=20 truncated sections 14/19/20 mid-expansion.)
                const int MAX_PASSES_PER_PAIR = 50;
                int passNum = 0;
                bool pairAborted = false;

                while (passNum < MAX_PASSES_PER_PAIR && !pairAborted)
                {
                    passNum++;

                    var infoTask = new StoryInfoTask(info);
                    yield return Toolbox.NetworkManager.Connect(infoTask, _ => { });
                    yield return new WaitForSeconds(pace);
                    if (!infoTask.isServerResultCodeOK())
                    {
                        Plugin.Log.LogWarning(
                            $"StorySweep[{label}]: section {section.Id} chara={charaId?.ToString() ?? "(none)"} /info rc={infoTask.GetResultCode()}; skipping pair.");
                        break;
                    }

                    // Filter on IsReleased to skip chapters the server hasn't unlocked yet
                    // (/start on those returns rc=500). Include no-battle chapters
                    // (battle_exists=false — story epilogues like Bloodcraft ch.15):
                    // they need /finish to mark cleared. IsEnableBattleSkip is
                    // battle_exists && is_skip_enabled, so it excludes no-battle chapters —
                    // handle them via the !ExistsBattle branch.
                    // Exclude story_ids we've already processed this session so we don't
                    // loop forever if the server re-reports them as "released".
                    var chapterList = (Data.StoryInfo?.ChapterDataList ?? new List<StoryChapterData>())
                        .Where(c => c != null
                                    && c.IsReleased
                                    && !c.IsLocked
                                    && !c.IsMaintenanceChapter
                                    && (c.IsEnableBattleSkip || !c.ExistsBattle)
                                    && !processedStoryIds.Contains(c.StoryId))
                        .OrderBy(c => c.ChapterRowNum)
                        .ThenBy(c => c.ChapterId, System.StringComparer.Ordinal)
                        .ToList();

                    if (chapterList.Count == 0)
                    {
                        if (passNum > 1)
                        {
                            Plugin.Log.LogInfo(
                                $"StorySweep[{label}]: section={section.Id} chara={charaId?.ToString() ?? "(none)"} stabilized after {passNum - 1} pass(es), {processedStoryIds.Count} chapter(s) processed.");
                        }
                        break;
                    }

                    Plugin.Log.LogInfo(
                        $"StorySweep[{label}]: section={section.Id} ({si + 1}/{sections.Count}) " +
                        $"chara={charaId?.ToString() ?? "(none)"} ({ci + 1}/{charaIds.Count}) " +
                        $"pass {passNum} → {chapterList.Count} new chapter(s).");

                    // Circuit breaker: if /start fails N times in a row, the chara is gated
                    // by progression we can't bypass. Break out of this pair entirely.
                    const int CONSECUTIVE_START_FAIL_THRESHOLD = 10;
                    int consecutiveStartFail = 0;

                    for (int chi = 0; chi < chapterList.Count; chi++)
                    {
                        var chapter = chapterList[chi];
                        info.SetChapter(chapter, null);
                        chapters++;
                        // Mark processed up front so even on failure we don't retry
                        // the same chapter on subsequent passes within this pair.
                        processedStoryIds.Add(chapter.StoryId);

                        var startTask = new StoryStartTask(info);
                    yield return Toolbox.NetworkManager.Connect(startTask, _ => { });
                    yield return new WaitForSeconds(pace);
                    if (startTask.isServerResultCodeOK())
                    {
                        startOk++;
                        consecutiveStartFail = 0;
                    }
                    else
                    {
                        startFail++;
                        consecutiveStartFail++;
                        Plugin.Log.LogWarning(
                            $"StorySweep[{label}]: story_id={chapter.StoryId} /start rc={startTask.GetResultCode()} — skipping /finish.");
                        if (consecutiveStartFail >= CONSECUTIVE_START_FAIL_THRESHOLD)
                        {
                            Plugin.Log.LogWarning(
                                $"StorySweep[{label}]: {CONSECUTIVE_START_FAIL_THRESHOLD} consecutive /start failures in section={section.Id} chara={charaId?.ToString() ?? "(none)"} — aborting remaining {chapterList.Count - chi - 1} chapter(s) of this pair, moving on.");
                            pairAborted = true;
                            break;
                        }
                        continue;
                    }

                    // Tutorial-play unlock: the play-shape with null recovery_data is ONLY
                    // accepted by the server for actual class tutorials, which per the client's
                    // own definition (StoryChapterData.IsClassTutorial) means (section_id=1,
                    // chapter_id="1") and nothing else. For chapter 1 of any other section,
                    // the server returns rc=205 (observed 2026-05-25). Use the canonical
                    // IsClassTutorial flag rather than a heuristic.
                    bool isTutorialUnlock =
                        chapter.IsClassTutorial
                        && charaId.HasValue
                        && currentChapterByChara.TryGetValue(charaId.Value, out int cur)
                        && cur <= 1
                        && chapter.ClearStatus == StoryChapterData.ChapterClearStatus.NotCleared;

                    var finishTask = new StoryFinishTask(info);
                    if (isTutorialUnlock)
                    {
                        Plugin.Log.LogInfo(
                            $"StorySweep[{label}]: story_id={chapter.StoryId} chara={charaId} is class-tutorial — using play-shape /finish to unlock progression.");
                        finishTask.Params = new StoryFinishTask.StoryFinishTaskParam
                        {
                            story_id = chapter.StoryId,
                            is_finish = 1,
                            evolve_count = 1,
                            total_turn = 3,
                            deck_no = 0,
                            use_build_deck = 0,
                            deck_format = 1,
                            class_id = charaId.Value,
                            mission = new Dictionary<string, int>(),
                            recovery_data = null,
                            prosessing_time_data = null,
                        };
                    }
                    else
                    {
                        finishTask.SetParameterNoBattle(
                            isFinish: true,
                            isSelectAnotherEnding: false,
                            chosenUnlockChapterId: null);
                    }
                    yield return Toolbox.NetworkManager.Connect(finishTask, _ => { });
                    yield return new WaitForSeconds(pace);
                    if (finishTask.isServerResultCodeOK())
                    {
                        finishOk++;
                        if (isTutorialUnlock) tutorialPlayOk++;
                    }
                    else
                    {
                        finishFail++;
                        if (isTutorialUnlock) tutorialPlayFail++;
                        Plugin.Log.LogWarning(
                            $"StorySweep[{label}]: story_id={chapter.StoryId} /finish rc={finishTask.GetResultCode()}{(isTutorialUnlock ? " (tutorial-play attempt)" : "")}.");
                    }
                    }
                }

                if (passNum >= MAX_PASSES_PER_PAIR && !pairAborted)
                {
                    Plugin.Log.LogWarning(
                        $"StorySweep[{label}]: section={section.Id} chara={charaId?.ToString() ?? "(none)"} hit MAX_PASSES_PER_PAIR={MAX_PASSES_PER_PAIR} — there may be more chapters unlockable on a future sweep run.");
                }
            }
        }

        Plugin.Log.LogInfo(
            $"StorySweep[{label}]: complete. chapters={chapters} " +
            $"start_ok={startOk}/{startOk + startFail} finish_ok={finishOk}/{finishOk + finishFail} " +
            $"tutorial_play_ok={tutorialPlayOk}/{tutorialPlayOk + tutorialPlayFail}");
    }

    private static StoryEntranceType ApiToEntrance(StoryApiType apiType) => apiType switch
    {
        StoryApiType.MainStory => StoryEntranceType.MainStory,
        StoryApiType.LimitedStory => StoryEntranceType.LimitedStory,
        StoryApiType.EventStory => StoryEntranceType.EventStory,
        _ => StoryEntranceType.None,
    };
}

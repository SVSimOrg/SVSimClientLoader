using System.Collections.Generic;
using System.IO;
using LitJson;

namespace SVSimLoader;

internal static class StoryProgressWriter
{
    private static readonly object _lock = new object();

    // Wire URL suffix → story_api_type discriminator (1=Main, 2=Limited, 3=Event)
    public static bool TryStoryApiTypeFromUrl(string url, out int apiType)
    {
        if (url.EndsWith("/main_story/info"))    { apiType = 1; return true; }
        if (url.EndsWith("/limited_story/info")) { apiType = 2; return true; }
        if (url.EndsWith("/event_story/info"))   { apiType = 3; return true; }
        apiType = 0; return false;
    }

    // Entry point called from ExaminationPatches with the raw LitJson-serialised envelope string.
    public static void HandleStoryInfoResponse(string sessionDir, string url, string responseBodyJson)
    {
        if (!TryStoryApiTypeFromUrl(url, out var apiType)) return;
        if (string.IsNullOrEmpty(responseBodyJson)) return;

        JsonData root;
        try { root = JsonMapper.ToObject(responseBodyJson); }
        catch { return; }
        if (root == null || !root.IsObject) return;

        // Unwrap envelope { data_headers, data } if present (same pattern as CaptureWriter).
        JsonData inner = root;
        if (root.Keys.Contains("data") && root["data"] != null && root["data"].IsObject)
            inner = root["data"];

        // Wire field: "story_master_list" (confirmed in StoryInfoTask.cs:53 + live captures).
        if (!inner.Keys.Contains("story_master_list")) return;
        JsonData list = inner["story_master_list"];
        if (list == null || !list.IsArray) return;

        var newRows = new List<Dictionary<string, object>>();
        for (int i = 0; i < list.Count; i++)
        {
            JsonData chapter = list[i];
            if (chapter == null || !chapter.IsObject) continue;
            if (!chapter.Keys.Contains("story_id")) continue;

            // Chapter-level story_id arrives as a JSON STRING on the wire (e.g. "362"),
            // even though sub_chapters.story_id is an int. Capture-verified 2026-06-23.
            JsonData sidVal = chapter["story_id"];
            int storyId;
            if (sidVal.IsString)
            {
                if (!int.TryParse((string)sidVal, out storyId)) continue;
            }
            else if (sidVal.IsInt)  { storyId = (int)sidVal; }
            else if (sidVal.IsLong) { storyId = (int)(long)sidVal; }
            else continue;

            // is_finish / is_skipped are JSON booleans at chapter level (capture-verified).
            bool isFinish  = chapter.Keys.Contains("is_finish")  && ReadBoolOrInt(chapter["is_finish"]);
            bool isSkipped = chapter.Keys.Contains("is_skipped") && ReadBoolOrInt(chapter["is_skipped"]);

            newRows.Add(new Dictionary<string, object>
            {
                { "story_api_type", apiType  },
                { "story_id",       storyId  },
                { "sub_chapter_id", null     },
                { "is_finish",      isFinish  },
                { "is_skipped",     isSkipped }
            });

            // sub_chapters: story_id=int, sub_chapter_id=int, is_finish=int(0/1), no is_skipped.
            // Capture-verified 2026-06-23 against traffic_prod_626_story.ndjson.
            if (chapter.Keys.Contains("sub_chapters"))
            {
                JsonData subs = chapter["sub_chapters"];
                if (subs != null && subs.IsArray)
                {
                    for (int j = 0; j < subs.Count; j++)
                    {
                        JsonData sub = subs[j];
                        if (sub == null || !sub.IsObject) continue;
                        if (!sub.Keys.Contains("sub_chapter_id")) continue;
                        JsonData scidVal = sub["sub_chapter_id"];
                        int subId;
                        if (scidVal.IsInt)       { subId = (int)scidVal; }
                        else if (scidVal.IsLong) { subId = (int)(long)scidVal; }
                        else continue;

                        // Sub-chapter is_finish is int (0/1) on the wire, not a bool.
                        bool subFinish = sub.Keys.Contains("is_finish") && ReadBoolOrInt(sub["is_finish"]);

                        // No is_skipped at sub-chapter level on the wire — always false.
                        newRows.Add(new Dictionary<string, object>
                        {
                            { "story_api_type", apiType   },
                            { "story_id",       storyId   },
                            { "sub_chapter_id", (object)subId },
                            { "is_finish",      subFinish  },
                            { "is_skipped",     false      }
                        });
                    }
                }
            }
        }

        if (newRows.Count == 0) return;

        var path = Path.Combine(sessionDir, "story_progress.json");
        lock (_lock)
        {
            // Dedupe key: (story_api_type, story_id, sub_chapter_id ?? -1). Last write wins.
            var merged = new Dictionary<string, Dictionary<string, object>>();
            if (File.Exists(path))
            {
                try
                {
                    JsonData existing = JsonMapper.ToObject(File.ReadAllText(path));
                    if (existing != null && existing.IsObject && existing.Keys.Contains("rows"))
                    {
                        JsonData existRows = existing["rows"];
                        if (existRows != null && existRows.IsArray)
                        {
                            for (int k = 0; k < existRows.Count; k++)
                            {
                                JsonData r = existRows[k];
                                if (r == null || !r.IsObject) continue;
                                int apiT = r.Keys.Contains("story_api_type") ? ReadIntVal(r["story_api_type"]) : 0;
                                int sid  = r.Keys.Contains("story_id")       ? ReadIntVal(r["story_id"])       : 0;
                                int scid = (r.Keys.Contains("sub_chapter_id") && r["sub_chapter_id"] != null
                                            && !r["sub_chapter_id"].IsBoolean && !r["sub_chapter_id"].IsString
                                            && (r["sub_chapter_id"].IsInt || r["sub_chapter_id"].IsLong))
                                           ? ReadIntVal(r["sub_chapter_id"]) : -1;
                                string key = apiT + "," + sid + "," + scid;
                                merged[key] = new Dictionary<string, object>
                                {
                                    { "story_api_type", apiT },
                                    { "story_id",       sid  },
                                    { "sub_chapter_id", scid == -1 ? null : (object)scid },
                                    { "is_finish",      r.Keys.Contains("is_finish")  && ReadBoolOrInt(r["is_finish"])  },
                                    { "is_skipped",     r.Keys.Contains("is_skipped") && ReadBoolOrInt(r["is_skipped"]) }
                                };
                            }
                        }
                    }
                }
                catch { /* corrupt file → overwrite */ }
            }

            foreach (var row in newRows)
            {
                int rowApiType  = (int)row["story_api_type"];
                int rowStoryId  = (int)row["story_id"];
                int rowScid     = row["sub_chapter_id"] is int sc ? sc : -1;
                string rowKey   = rowApiType + "," + rowStoryId + "," + rowScid;
                merged[rowKey]  = row;
            }

            // Materialise to a concrete List so JsonMapper writes a JSON array; reflecting a
            // Dictionary.ValueCollection instead emits {"Count": N} (the only public property
            // JsonMapper sees on ValueCollection).
            var output = new Dictionary<string, object>
            {
                { "rows", new List<Dictionary<string, object>>(merged.Values) }
            };
            File.WriteAllText(path, JsonMapper.ToJson(output));
        }
    }

    private static bool ReadBoolOrInt(JsonData v)
    {
        if (v == null) return false;
        if (v.IsBoolean) return (bool)v;
        if (v.IsInt)  return (int)v  != 0;
        if (v.IsLong) return (long)v != 0;
        return false;
    }

    private static int ReadIntVal(JsonData v)
    {
        if (v == null) return 0;
        if (v.IsInt)  return (int)v;
        if (v.IsLong) return (int)(long)v;
        return 0;
    }
}

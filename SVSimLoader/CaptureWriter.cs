using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using LitJson;

namespace SVSimLoader;

internal static class CaptureWriter
{
    private static readonly object _lock = new object();
    private static string _sessionDir;
    private static string _trafficPath;
    private static string _battleTrafficPath;
    private static string _cardsPath;
    private static string _userDataPath;
    private static string _sbsPath;
    private static string _spinProbePath;
    private static readonly HashSet<string> _seenSbsIds = new HashSet<string>();
    private static ulong _lastSeenSteamId;

    public static string SessionDirectory => _sessionDir;

    public static void Initialize()
    {
        string root = Path.Combine(Paths.BepInExRootPath, "svsim-captures");
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string host = SanitizeForPath(ExtractHost(SvSimConfig.ApplicationUrl));
        string session = string.IsNullOrEmpty(host) ? timestamp : $"{timestamp}_{host}";
        _sessionDir = Path.Combine(root, session);
        Directory.CreateDirectory(_sessionDir);
        _trafficPath = Path.Combine(_sessionDir, "traffic.ndjson");
        _battleTrafficPath = Path.Combine(_sessionDir, "battle-traffic.ndjson");
        _cardsPath = Path.Combine(_sessionDir, "cards.json");
        _userDataPath = Path.Combine(_sessionDir, "user-data.json");
        _sbsPath = Path.Combine(_sessionDir, "special-battle-settings.ndjson");
        _spinProbePath = Path.Combine(_sessionDir, "spin-rng.ndjson");
    }

    /// <summary>
    /// Append one row of the spin/shared-RNG diagnostic (EnableSpinProbe). `event` is "receive"
    /// (OperateReceive.StartOperate, logged before the spin crank) or "send" (NetworkBattleSender.EmitMsg).
    /// `spin` is the inbound crank count (receive only; -1 when not applicable) and `count` is the
    /// cumulative BattleManagerBase.stableRandomCount tally (-1 when no battle manager is live).
    /// Offline, per-turn `count` deltas vs `spin` settle whether prod authors spin from server-side
    /// simulation or it is derivable from the active player's wire data. See
    /// docs/audits/battle-node-spin-rng-model-2026-06-04.md.
    /// </summary>
    public static void AppendSpinProbe(string eventKind, string uri, int spin, int count)
    {
        string line = JsonMapper.ToJson(new Dictionary<string, object>
        {
            { "ts", DateTime.UtcNow.ToString("o") },
            { "event", eventKind },
            { "uri", uri },
            { "spin", spin },
            { "count", count },
        });
        lock (_lock)
        {
            File.AppendAllText(_spinProbePath, line + "\n");
        }
    }

    private static string ExtractHost(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try { return new Uri(url).Host; }
        catch { return null; }
    }

    private static string SanitizeForPath(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    public static void WriteCards(string json)
    {
        lock (_lock)
        {
            File.WriteAllText(_cardsPath, json);
        }
    }

    /// <summary>
    /// Append a single special_battle_setting payload to special-battle-settings.ndjson,
    /// keyed-deduped by sbs id (in-memory, per session). Story chapters reuse sbs rows
    /// heavily (one row spans many chapters across many classes), so dedup keeps the
    /// file compact. Bootstrap-side join with /info captures resolves the
    /// chapter → sbs_id mapping separately.
    /// </summary>
    public static void AppendSpecialBattleSetting(string sbsId, JsonData sbsPayload)
    {
        if (string.IsNullOrEmpty(sbsId) || sbsPayload == null) return;
        lock (_lock)
        {
            if (!_seenSbsIds.Add(sbsId)) return;
            var ts = DateTime.UtcNow.ToString("o");
            var line = "{\"ts\":\"" + ts
                + "\",\"id\":\"" + EscapeJsonString(sbsId)
                + "\",\"payload\":" + sbsPayload.ToJson() + "}";
            File.AppendAllText(_sbsPath, line + "\n");
        }
    }

    private static string EscapeJsonString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    public static void AppendTraffic(string direction, string url, bool encrypted, string body)
    {
        string envelope = JsonMapper.ToJson(new Dictionary<string, object>
        {
            { "ts", DateTime.UtcNow.ToString("o") },
            { "direction", direction },
            { "url", url },
            { "encrypted", encrypted },
        });
        AppendLineWithBody(_trafficPath, envelope, body);
    }

    public static void AppendBattleTraffic(string direction, string uri, string body)
    {
        string envelope = JsonMapper.ToJson(new Dictionary<string, object>
        {
            { "ts", DateTime.UtcNow.ToString("o") },
            { "direction", direction },
            { "uri", uri },
        });
        AppendLineWithBody(_battleTrafficPath, envelope, body);
    }

    /// <summary>
    /// Track the most recent steam_id seen on an outgoing request body. We need it to enrich
    /// the user-data dump (/load/index response doesn't carry steam_id but the import endpoint
    /// needs it to link the viewer).
    /// </summary>
    public static void RecordSteamId(ulong steamId)
    {
        if (steamId != 0) _lastSeenSteamId = steamId;
    }

    /// <summary>
    /// Extract essential viewer fields from a /load/index response and write them as a clean
    /// JSON file matching the /admin/import_viewer request shape. Skipped silently when no
    /// steam_id has been seen yet (need at least one prior request to know the link).
    /// </summary>
    public static void WriteUserDataFromLoadIndex(JsonData loadIndexData)
    {
        if (_lastSeenSteamId == 0)
        {
            Plugin.Log?.LogWarning("DumpUserData: no steam_id seen yet, skipping user-data dump.");
            return;
        }

        try
        {
            // SetResponseData hands us the FULL response envelope { data_headers, data }; the
            // viewer payload (user_info, user_crystal_count, user_card_list, ...) lives under the
            // inner `data` key. Descend into it before extracting — same as
            // ExaminationPatches.TryExtractSpecialBattleSettings does. Without this every SafeGet
            // below misses and the dump contains nothing but steam_id. The inner payload has no
            // top-level `data` key of its own, so this is safe if a caller ever pre-strips it.
            if (loadIndexData != null && loadIndexData.IsObject && loadIndexData.Keys.Contains("data"))
            {
                loadIndexData = loadIndexData["data"];
            }

            var dump = new Dictionary<string, object>
            {
                { "steam_id", _lastSeenSteamId }
            };

            var userInfo = SafeGet(loadIndexData, "user_info");
            if (userInfo != null)
            {
                Copy(userInfo, "name", dump, "display_name");
                Copy(userInfo, "country_code", dump, "country_code");
                Copy(userInfo, "selected_emblem_id", dump, "selected_emblem_id");
                Copy(userInfo, "selected_degree_id", dump, "selected_degree_id");
            }

            var tutorial = SafeGet(loadIndexData, "user_tutorial");
            if (tutorial != null) Copy(tutorial, "tutorial_step", dump, "tutorial_state");

            var crystal = SafeGet(loadIndexData, "user_crystal_count");
            if (crystal != null)
            {
                var cur = new Dictionary<string, object>();
                Copy(crystal, "crystal", cur, "crystals");
                Copy(crystal, "rupy", cur, "rupees");
                Copy(crystal, "red_ether", cur, "red_ether");
                if (cur.Count > 0) dump["currency"] = cur;
            }

            ExtractIdArray(loadIndexData, "user_sleeve_list", "sleeve_id", dump, "owned_sleeve_ids");
            ExtractIdArray(loadIndexData, "user_emblem_list", "emblem_id", dump, "owned_emblem_ids");
            ExtractIdArray(loadIndexData, "user_degree_list", "degree_id", dump, "owned_degree_ids");
            ExtractMyPageList(loadIndexData, dump);
            ExtractOwnedLeaderSkins(loadIndexData, dump);
            ExtractClasses(loadIndexData, dump);
            ExtractOwnedCards(loadIndexData, dump);
            ExtractItems(loadIndexData, dump);
            ExtractDecks(loadIndexData, dump);
            // NOTE: user_mission_list, user_achievement_list, and mission_meta are NOT present on
            // /load/index. They are served by /mission/info and merged by WriteMissionInfoData
            // (wired in ExaminationPatches). mission_receive_type is emitted there from the top-level
            // data field (string-encoded int, e.g. "0"). has_received_pick_two_mission and
            // mission_change_time remain deferred until their wire location is confirmed.
            // See ImportMissionMeta / spec Open Question 1.

            lock (_lock)
            {
                File.WriteAllText(_userDataPath, JsonMapper.ToJson(dump));
            }
            Plugin.Log?.LogInfo($"Dumped user data to user-data.json (steam_id={_lastSeenSteamId}).");
        }
        catch (Exception e)
        {
            Plugin.Log?.LogError($"DumpUserData failed: {e}");
        }
    }

    /// <summary>
    /// Extract missions and achievements from a /mission/info response and merge them into the
    /// existing user-data.json (written earlier by WriteUserDataFromLoadIndex). Called from
    /// ExaminationPatches whenever a /mission/info response is received with DumpUserData enabled.
    /// Missions and achievements do NOT appear on /load/index — they live on this endpoint only.
    /// </summary>
    public static void WriteMissionInfoData(JsonData missionInfoData)
    {
        if (_lastSeenSteamId == 0) return;
        try
        {
            // Unwrap envelope { data_headers, data } if present.
            if (missionInfoData != null && missionInfoData.IsObject && missionInfoData.Keys.Contains("data"))
                missionInfoData = missionInfoData["data"];

            // Hold _lock for the entire read-extract-write sequence so no concurrent writer
            // (e.g. WriteUserDataFromLoadIndex) can overwrite the file between our read and write.
            // The extract work below is pure JSON manipulation — no I/O — so holding through it is cheap.
            lock (_lock)
            {
                // Read the existing dump (if any) so we can merge new keys into it.
                Dictionary<string, object> dump;
                if (!File.Exists(_userDataPath))
                {
                    dump = new Dictionary<string, object> { { "steam_id", _lastSeenSteamId } };
                }
                else
                {
                    try
                    {
                        var existing = JsonMapper.ToObject(File.ReadAllText(_userDataPath));
                        dump = new Dictionary<string, object> { { "steam_id", _lastSeenSteamId } };
                        if (existing != null && existing.IsObject)
                        {
                            // Convert JsonData values to native types — JsonMapper.ToJson(Dictionary<string,object>)
                            // reflects native types but throws "Can't add a property here" on boxed JsonData values.
                            foreach (string k in existing.Keys)
                                dump[k] = JsonDataToNative(existing[k]);
                        }
                    }
                    catch
                    {
                        dump = new Dictionary<string, object> { { "steam_id", _lastSeenSteamId } };
                    }
                }

                var missions = ExtractMissions(missionInfoData);
                if (missions.Count > 0) dump["missions"] = missions;

                var achievements = ExtractAchievements(missionInfoData);
                if (achievements.Count > 0) dump["achievements"] = achievements;

                // mission_meta: emit mission_receive_type (confirmed present on /mission/info as a
                // string-encoded int, e.g. "0"). has_received_pick_two_mission and mission_change_time
                // are absent from captures — defer until their wire location is confirmed.
                // TODO: add has_received_pick_two_mission and mission_change_time to mission_meta
                //       once their wire location (endpoint + field name) is confirmed.
                var missionMeta = new Dictionary<string, object>();
                CopyIntOrString(missionInfoData, "mission_receive_type", missionMeta);
                if (missionMeta.Count > 0) dump["mission_meta"] = missionMeta;

                File.WriteAllText(_userDataPath, JsonMapper.ToJson(dump));
            }
            Plugin.Log?.LogInfo($"Merged mission/achievement data into user-data.json.");
        }
        catch (Exception e)
        {
            Plugin.Log?.LogError($"WriteMissionInfoData failed: {e}");
        }
    }

    private static List<Dictionary<string, object>> ExtractMissions(JsonData data)
    {
        var result = new List<Dictionary<string, object>>();
        var list = SafeGet(data, "user_mission_list");
        if (list == null || !list.IsArray) return result;
        for (int i = 0; i < list.Count; i++)
        {
            var m = list[i];
            var row = new Dictionary<string, object>();
            CopyInt(m, "mission_id", row);
            CopyInt(m, "mission_status", row);
            CopyInt(m, "total_count", row);
            if (row.Count > 0) result.Add(row);
        }
        return result;
    }

    private static List<Dictionary<string, object>> ExtractAchievements(JsonData data)
    {
        var result = new List<Dictionary<string, object>>();
        var list = SafeGet(data, "user_achievement_list");
        if (list == null || !list.IsArray) return result;
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            var row = new Dictionary<string, object>();
            // Achievement fields arrive as JSON strings ("12") — use CopyIntOrString which
            // parses the string representation if the raw value is not already an int/long.
            CopyIntOrString(a, "achievement_type", row);
            CopyIntOrString(a, "level", row);
            CopyIntOrString(a, "now_achieved_level", row);
            CopyIntOrString(a, "result_announce_saw_level", row);
            CopyIntOrString(a, "total_count", row);
            if (row.Count > 0) result.Add(row);
        }
        return result;
    }

    /// <summary>Copy a field that is expected to be int/long-typed on the wire.</summary>
    private static void CopyInt(JsonData source, string key, Dictionary<string, object> dest)
    {
        var val = SafeGet(source, key);
        if (val == null) return;
        if (val.IsInt) dest[key] = (int)val;
        else if (val.IsLong) dest[key] = (int)(long)val;
    }

    /// <summary>
    /// Copy a field that may arrive as a JSON string containing an integer (e.g. "12").
    /// Achievement fields on /mission/info use this encoding.
    /// </summary>
    private static void CopyIntOrString(JsonData source, string key, Dictionary<string, object> dest)
    {
        var val = SafeGet(source, key);
        if (val == null) return;
        if (val.IsInt) { dest[key] = (int)val; return; }
        if (val.IsLong) { dest[key] = (int)(long)val; return; }
        if (val.IsString)
        {
            if (int.TryParse((string)val, out int parsed)) dest[key] = parsed;
            // else: non-numeric string — skip to avoid sending garbage
        }
    }

    private static JsonData SafeGet(JsonData data, string key)
    {
        if (data == null || !data.IsObject) return null;
        try
        {
            return data.Keys.Contains(key) ? data[key] : null;
        }
        catch
        {
            return null;
        }
    }

    // Recursively unwrap a JsonData tree into native .NET types so JsonMapper.ToJson can re-emit it
    // via its reflection path. JsonMapper does NOT recognise boxed JsonData values inside a generic
    // Dictionary<string,object>, which manifests as "Can't add a property here" at serialise time.
    private static object JsonDataToNative(JsonData d)
    {
        if (d == null) return null;
        if (d.IsObject)
        {
            var dict = new Dictionary<string, object>();
            foreach (string k in d.Keys) dict[k] = JsonDataToNative(d[k]);
            return dict;
        }
        if (d.IsArray)
        {
            var list = new List<object>();
            for (int i = 0; i < d.Count; i++) list.Add(JsonDataToNative(d[i]));
            return list;
        }
        if (d.IsString) return (string)d;
        if (d.IsInt) return (int)d;
        if (d.IsLong) return (long)d;
        if (d.IsBoolean) return (bool)d;
        if (d.IsDouble) return (double)d;
        return null;
    }

    private static void Copy(JsonData source, string srcKey, Dictionary<string, object> dest, string destKey)
    {
        var val = SafeGet(source, srcKey);
        if (val == null) return;
        if (val.IsInt) dest[destKey] = (int)val;
        else if (val.IsLong) dest[destKey] = (long)val;
        else if (val.IsString) dest[destKey] = (string)val;
        else if (val.IsBoolean) dest[destKey] = (bool)val;
        else if (val.IsDouble) dest[destKey] = (double)val;
    }

    /// <summary>
    /// Read a JsonData scalar as a long, accepting IsInt / IsLong / IsString (parsed).
    /// The wire ships several numeric fields (card_id, item_id, their `number`, even some
    /// cosmetic ids on some accounts) as JSON strings; pre-2026-06-26 the extractors only
    /// accepted IsInt/IsLong and silently dropped string-encoded rows, producing dumps
    /// with no `owned_cards` / `items`.
    /// </summary>
    private static bool TryReadLong(JsonData v, out long result)
    {
        result = 0;
        if (v == null) return false;
        if (v.IsInt) { result = (int)v; return true; }
        if (v.IsLong) { result = (long)v; return true; }
        if (v.IsString) return long.TryParse((string)v, out result);
        return false;
    }

    private static void ExtractIdArray(JsonData data, string listKey, string idField,
        Dictionary<string, object> dump, string destKey)
    {
        var list = SafeGet(data, listKey);
        if (list == null || !list.IsArray) return;
        var ids = new List<long>();
        for (int i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            if (TryReadLong(SafeGet(entry, idField), out long id)) ids.Add(id);
        }
        if (ids.Count > 0) dump[destKey] = ids;
    }

    private static void ExtractMyPageList(JsonData data, Dictionary<string, object> dump)
    {
        // Wire shape is string[] per the audit (LoadDetail.cs:387-392 calls .ToString()).
        var list = SafeGet(data, "user_mypage_list");
        if (list == null || !list.IsArray) return;
        var ids = new List<int>();
        for (int i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            if (entry == null) continue;
            if (entry.IsInt) ids.Add((int)entry);
            else if (entry.IsLong) ids.Add((int)(long)entry);
            else if (entry.IsString && int.TryParse((string)entry, out int parsed)) ids.Add(parsed);
        }
        if (ids.Count > 0) dump["owned_mypage_background_ids"] = ids;
    }

    private static void ExtractOwnedLeaderSkins(JsonData data, Dictionary<string, object> dump)
    {
        var list = SafeGet(data, "user_leader_skin_list");
        if (list == null || !list.IsArray) return;
        var ids = new List<long>();
        for (int i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            var isOwned = SafeGet(entry, "is_owned");
            bool owned = isOwned != null && (
                (isOwned.IsBoolean && (bool)isOwned) ||
                (isOwned.IsInt && (int)isOwned != 0) ||
                (isOwned.IsLong && (long)isOwned != 0) ||
                (isOwned.IsString && (string)isOwned != "0" && !string.IsNullOrEmpty((string)isOwned)));
            if (!owned) continue;
            if (TryReadLong(SafeGet(entry, "leader_skin_id"), out long id)) ids.Add(id);
        }
        if (ids.Count > 0) dump["owned_leader_skin_ids"] = ids;
    }

    private static void ExtractClasses(JsonData data, Dictionary<string, object> dump)
    {
        var list = SafeGet(data, "user_class_list");
        if (list == null || !list.IsArray) return;
        var classes = new List<Dictionary<string, object>>();
        for (int i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            var classId = SafeGet(entry, "class_id");
            if (classId == null) continue;
            var c = new Dictionary<string, object>();
            if (classId.IsInt) c["class_id"] = (int)classId;
            else if (classId.IsLong) c["class_id"] = (int)(long)classId;
            else continue;
            Copy(entry, "level", c, "level");
            Copy(entry, "exp", c, "exp");
            classes.Add(c);
        }
        if (classes.Count > 0) dump["classes"] = classes;
    }

    private static void ExtractOwnedCards(JsonData data, Dictionary<string, object> dump)
    {
        var list = SafeGet(data, "user_card_list");
        if (list == null || !list.IsArray) return;
        var cards = new List<Dictionary<string, object>>();
        for (int i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            if (!TryReadLong(SafeGet(entry, "card_id"), out long cardId)) continue;

            var c = new Dictionary<string, object> { { "card_id", cardId } };

            if (TryReadLong(SafeGet(entry, "number"), out long num)) c["count"] = (int)num;

            var prot = SafeGet(entry, "is_protected");
            if (prot != null)
            {
                c["is_protected"] =
                    (prot.IsBoolean && (bool)prot) ||
                    (prot.IsInt && (int)prot != 0) ||
                    (prot.IsLong && (long)prot != 0) ||
                    (prot.IsString && (string)prot != "0" && !string.IsNullOrEmpty((string)prot));
            }
            cards.Add(c);
        }
        if (cards.Count > 0) dump["owned_cards"] = cards;
    }

    private static void ExtractItems(JsonData data, Dictionary<string, object> dump)
    {
        var list = SafeGet(data, "user_item_list");
        if (list == null || !list.IsArray) return;
        var items = new List<Dictionary<string, object>>();
        for (int i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            if (!TryReadLong(SafeGet(entry, "item_id"), out long itemId)) continue;
            var item = new Dictionary<string, object> { { "item_id", itemId } };
            if (TryReadLong(SafeGet(entry, "number"), out long num)) item["count"] = (int)num;
            items.Add(item);
        }
        if (items.Count > 0) dump["items"] = items;
    }

    // /load/index splits decks into one container per format; the format is the KEY, not a
    // per-deck field. Values mirror the wire deck_format codes (Wizard/Data.cs FormatConvertApi).
    private struct DeckFormatKey
    {
        public string Key;
        public int Format;
        public DeckFormatKey(string key, int format) { Key = key; Format = format; }
    }

    private static readonly DeckFormatKey[] DeckFormatKeys =
    {
        new DeckFormatKey("user_deck_rotation",     1),
        new DeckFormatKey("user_deck_unlimited",    2),
        new DeckFormatKey("user_deck_pre_rotation", 3),
        new DeckFormatKey("user_deck_crossover",    4),
        new DeckFormatKey("user_deck_my_rotation",  5),
    };

    private static void ExtractDecks(JsonData data, Dictionary<string, object> dump)
    {
        var decks = new List<Dictionary<string, object>>();
        foreach (var fmt in DeckFormatKeys)
        {
            var container = SafeGet(data, fmt.Key);
            var deckList = SafeGet(container, "user_deck_list");
            if (deckList == null || !deckList.IsArray) continue;

            for (int i = 0; i < deckList.Count; i++)
            {
                var entry = deckList[i];
                // /load/index ships every deck slot, most of them empty placeholders. Skip the
                // empty ones — the import drops them anyway, and it keeps the dump to the few real
                // decks instead of ~100 empty slots.
                var cardArr = ExtractLongArray(entry, "card_id_array");
                if (cardArr == null || cardArr.Count == 0) continue;

                var d = new Dictionary<string, object> { { "deck_format", fmt.Format } };
                Copy(entry, "deck_no", d, "deck_no");
                Copy(entry, "deck_name", d, "deck_name");
                Copy(entry, "class_id", d, "class_id");
                Copy(entry, "sleeve_id", d, "sleeve_id");
                Copy(entry, "leader_skin_id", d, "leader_skin_id");
                Copy(entry, "is_random_leader_skin", d, "is_random_leader_skin");
                Copy(entry, "rotation_id", d, "my_rotation_id"); // UserDeck.rotation_id -> import my_rotation_id
                d["card_id_array"] = cardArr;
                decks.Add(d);
            }
        }
        if (decks.Count > 0) dump["decks"] = decks;
    }

    private static List<long> ExtractLongArray(JsonData entry, string key)
    {
        var arr = SafeGet(entry, key);
        if (arr == null || !arr.IsArray) return null;
        var ids = new List<long>();
        for (int i = 0; i < arr.Count; i++)
        {
            var v = arr[i];
            if (v == null) continue;
            if (v.IsInt) ids.Add((int)v);
            else if (v.IsLong) ids.Add((long)v);
        }
        return ids;
    }

    // Splice the body into the envelope as nested JSON (parseable) or escaped string
    // (fallback). Cannot route this through Dictionary<string,object> → JsonMapper.ToJson:
    // a LitJson.JsonData value inside such a dict makes the reflection serializer
    // mis-bracket and throw "Can't close an object here".
    private static void AppendLineWithBody(string path, string envelopeJson, string body)
    {
        string bodyJson;
        if (body == null)
        {
            bodyJson = "null";
        }
        else if (body.Length == 0)
        {
            bodyJson = "\"\"";
        }
        else
        {
            try { bodyJson = JsonMapper.ToObject(body).ToJson(); }
            catch { bodyJson = JsonMapper.ToJson(body); }
        }
        string trimmed = envelopeJson.Substring(0, envelopeJson.Length - 1);
        string line = trimmed + ",\"body\":" + bodyJson + "}";
        lock (_lock)
        {
            File.AppendAllText(path, line + "\n");
        }
    }
}

using System;
using System.Collections.Generic;
using BestHTTP.SocketIO;
using Cute;
using HarmonyLib;
using LitJson;
using MessagePack;
using Wizard;

namespace SVSimLoader.Patches;

[HarmonyPatch]
public static class ExaminationPatches
{
    [HarmonyPatch(typeof(NetworkTask), nameof(NetworkTask.SetResponseData))]
    [HarmonyPrefix]
    public static bool SetResponseData(JsonData data, NetworkTask __instance)
    {
        Plugin.Log.LogInfo($"← {__instance.Url}");
        if (SvSimConfig.EnableTrafficCapture)
        {
            CaptureWriter.AppendTraffic("response", __instance.Url, encrypted: false, body: data.ToJson());
        }
        if (SvSimConfig.DumpUserData && __instance.Url != null && __instance.Url.EndsWith("/load/index"))
        {
            // `data` is the FULL envelope { data_headers, data } (see TryExtractSpecialBattleSettings
            // + NetworkTask.cs:108-110). WriteUserDataFromLoadIndex descends into the inner `data`
            // key itself, so pass the envelope as-is.
            CaptureWriter.WriteUserDataFromLoadIndex(data);
        }
        if (SvSimConfig.DumpUserData && __instance.Url != null && __instance.Url.EndsWith("/mission/info"))
        {
            // user_mission_list and user_achievement_list are served by /mission/info, not /load/index.
            // Merge them into the existing user-data.json written by WriteUserDataFromLoadIndex.
            CaptureWriter.WriteMissionInfoData(data);
        }
        if (SvSimConfig.DumpUserData && __instance.Url != null && StoryProgressWriter.TryStoryApiTypeFromUrl(__instance.Url, out _))
        {
            // story_master_list (chapter + sub-chapter progress) is served by *_story/info endpoints.
            // Written to story_progress.json in the session dir for server-side import.
            try { StoryProgressWriter.HandleStoryInfoResponse(CaptureWriter.SessionDirectory, __instance.Url, data.ToJson()); }
            catch { /* don't let capture break the game */ }
        }
        if (SvSimConfig.SweepGachaExchange && __instance.Url != null && __instance.Url.EndsWith("/pack/info"))
        {
            GachaExchangeSweep.OnPackInfoResponse(data);
        }
        if (__instance.Url != null && IsStorySectionUrl(__instance.Url) &&
            (SvSimConfig.SweepMainStory || SvSimConfig.SweepLimitedStory || SvSimConfig.SweepEventStory))
        {
            StorySweep.OnSectionResponse(__instance.Url);
        }
        if (__instance.Url != null && __instance.Url.EndsWith("/mypage/refresh") &&
            (SvSimConfig.ProbeLimitedSection || SvSimConfig.ProbeEventSection))
        {
            StorySectionProbe.OnMypageRefreshResponse();
        }
        if (__instance.Url != null && IsStoryStartUrl(__instance.Url))
        {
            TryExtractSpecialBattleSettings(data);
        }
        return true;
    }

    private static bool IsStorySectionUrl(string url)
    {
        return url.EndsWith("/story/section")
            || url.EndsWith("/main_story/section")
            || url.EndsWith("/limited_story/section")
            || url.EndsWith("/event_story/section");
    }

    private static bool IsStoryStartUrl(string url)
    {
        return url.EndsWith("/main_story/start")
            || url.EndsWith("/limited_story/start")
            || url.EndsWith("/event_story/start");
    }

    /// <summary>
    /// /{family}/start responses, after envelope-unwrap, look like:
    ///   { "0": <slot>|[], "1": <slot>|[], ..., "mission_parameter": [...] }
    /// where each numeric key matches the index of the request's story_ids array. Each slot is either an
    /// empty array (chapter has no sbs assigned) or an object containing a special_battle_setting payload.
    /// Extract any sbs payloads and hand to CaptureWriter for dedup + append.
    ///
    /// NOTE: SetResponseData's `data` param is the FULL envelope (including data_headers wrapper) — see
    /// NetworkTask.cs:108-110 + getDataHeader at :513. Must descend into data["data"] first.
    /// </summary>
    private static void TryExtractSpecialBattleSettings(JsonData data)
    {
        try
        {
            if (data == null || !data.IsObject) return;
            JsonData inner = data.Keys.Contains("data") ? data["data"] : data;
            if (inner == null || !inner.IsObject) return;
            foreach (var keyObj in inner.Keys)
            {
                string key = keyObj;
                if (!int.TryParse(key, out _)) continue;
                var slot = inner[key];
                if (slot == null || !slot.IsObject || !slot.Keys.Contains("special_battle_setting")) continue;
                var sbs = slot["special_battle_setting"];
                if (sbs == null || !sbs.IsObject || !sbs.Keys.Contains("id")) continue;
                var id = sbs["id"].ToString();
                if (string.IsNullOrEmpty(id) || id == "0") continue;
                CaptureWriter.AppendSpecialBattleSetting(id, sbs);
            }
        }
        catch (Exception e)
        {
            Plugin.Log?.LogError($"TryExtractSpecialBattleSettings: {e}");
        }
    }

    [HarmonyPatch(typeof(NetworkTask), "CreateBody")]
    [HarmonyPrefix]
    public static bool CreateBody(NetworkTask __instance, bool encrypt)
    {
        Plugin.Log.LogInfo($"→ {__instance.Url} (encrypted={encrypt})");
        string body = JsonMapper.ToJson(__instance.Params);
        if (SvSimConfig.EnableTrafficCapture)
        {
            CaptureWriter.AppendTraffic("request", __instance.Url, encrypted: encrypt, body: body);
        }
        // Track the most recent steam_id on any outgoing request — needed by the user-data
        // dump (the /load/index response itself doesn't carry steam_id).
        if (SvSimConfig.DumpUserData)
        {
            TryRecordSteamIdFromRequestBody(body);
        }
        return true;
    }

    private static void TryRecordSteamIdFromRequestBody(string body)
    {
        if (string.IsNullOrEmpty(body)) return;
        try
        {
            JsonData parsed = JsonMapper.ToObject(body);
            if (parsed == null || !parsed.IsObject || !parsed.Keys.Contains("steam_id")) return;
            var val = parsed["steam_id"];
            if (val.IsLong) CaptureWriter.RecordSteamId((ulong)(long)val);
            else if (val.IsInt) CaptureWriter.RecordSteamId((ulong)(int)val);
        }
        catch
        {
            // Best-effort; some request bodies may not be JSON-shaped objects.
        }
    }

    [HarmonyPatch(typeof(CardMaster), MethodType.Constructor, typeof(List<CardCSVData>))]
    [HarmonyPostfix]
    public static void ExamineCardMaster(List<CardCSVData> cardList)
    {
        if (SvSimConfig.DumpCardDB)
        {
            CaptureWriter.WriteCards(JsonMapper.ToJson(cardList));
            Plugin.Log.LogInfo($"Dumped {cardList.Count} cards to cards.json");
        }
    }

    // Patch on the string-overload of EmitMsgPack — the single chokepoint all battle, room, and AI
    // msg emits funnel through. The URI-typed overload (line 1266) converts its enum arg to a string
    // and delegates here, so patching this one method covers every category without double-firing.
    //
    // Type[] disambiguation is mandatory because EmitMsgPack is overloaded; omitting it would let
    // Harmony bind the wrong overload and either miss room frames or double-log battle frames.
    //
    // TODO: EmitHandData (RealTimeNetworkAgent.cs:1296) routes select-target and slide-card events
    // through Socket.Emit("hand", ...) — a separate channel that this loader has never captured.
    // Rooms don't use it; only in-battle target/slide interactions do. Defer to a future phase.
    [HarmonyPatch(typeof(RealTimeNetworkAgent), nameof(RealTimeNetworkAgent.EmitMsgPack),
        new System.Type[] {
            typeof(string),
            typeof(RealTimeNetworkAgent.EmitCategory),
            typeof(Dictionary<string, object>),
            typeof(Action),
            typeof(bool),
            typeof(bool),
            typeof(int),
        })]
    [HarmonyPrefix]
    public static bool EmitSocketIoMsgPack(string uri,
        RealTimeNetworkAgent.EmitCategory emitCategory,
        Dictionary<string, object> info)
    {
        try
        {
            Plugin.Log.LogInfo($"→ msg {uri} (cat={emitCategory})");
            if (SvSimConfig.EnableBattleCapture)
            {
                CaptureWriter.AppendBattleTraffic("send", uri,
                    body: info == null ? null : JsonMapper.ToJson(info));
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }

        return true;
    }

    [HarmonyPatch(typeof(RealTimeNetworkAgent), "OnReceived")]
    [HarmonyPrefix]
    public static bool ReceiveBattleMsg(Packet packet)
    {
        try
        {
            byte[] bytes = packet.Attachments[0];
            string src = MessagePackSerializer.Deserialize<string>(bytes);
            string json = CryptAES.decryptForNode(src);
            Plugin.Log.LogInfo("← battle");
            if (SvSimConfig.EnableBattleCapture)
            {
                CaptureWriter.AppendBattleTraffic("receive", uri: null, body: json);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }

        return true;
    }
}

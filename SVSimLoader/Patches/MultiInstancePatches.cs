using Cute;
using HarmonyLib;

namespace SVSimLoader.Patches;

[HarmonyPatch]
public static class MultiInstancePatches
{
    // (1) Defeat the machine-wide single-instance guard. createMutex is private, so target it
    //     by string name. Prefix returning false skips the original (Application.Quit never runs).
    [HarmonyPatch(typeof(BootApp), "createMutex")]
    [HarmonyPrefix]
    public static bool SkipMutex()
    {
        if (!SvSimConfig.SecondaryInstance) return true; // primary/normal: run original
        Plugin.Log.LogWarning("Multi-instance: skipping BootApp.createMutex single-instance guard.");
        return false;
    }

    // (2) Redirect the three identity keys to the per-instance file store. Other keys fall
    //     through to the original (return true).
    private static bool IsIdentityKey(string key) =>
        key == "UDID" || key == "VIEWER_ID" || key == "SHORT_UDID";

    [HarmonyPatch(typeof(SavedataManager), nameof(SavedataManager.GetString))]
    [HarmonyPrefix]
    public static bool GetString(string key, string defaultValue, ref string __result)
    {
        if (!SvSimConfig.SecondaryInstance || !IsIdentityKey(key)) return true;
        __result = InstanceIdentityStore.TryGet(key, out var v) ? v : defaultValue;
        return false;
    }

    [HarmonyPatch(typeof(SavedataManager), nameof(SavedataManager.SetString))]
    [HarmonyPrefix]
    public static bool SetString(string key, string value)
    {
        if (!SvSimConfig.SecondaryInstance || !IsIdentityKey(key)) return true;
        InstanceIdentityStore.Set(key, value);
        return false;
    }

    [HarmonyPatch(typeof(SavedataManager), nameof(SavedataManager.GetInt))]
    [HarmonyPrefix]
    public static bool GetInt(string key, int defaultValue, ref int __result)
    {
        if (!SvSimConfig.SecondaryInstance || !IsIdentityKey(key)) return true;
        __result = InstanceIdentityStore.TryGet(key, out var v) && int.TryParse(v, out var i) ? i : defaultValue;
        return false;
    }

    [HarmonyPatch(typeof(SavedataManager), nameof(SavedataManager.SetInt))]
    [HarmonyPrefix]
    public static bool SetInt(string key, int value)
    {
        if (!SvSimConfig.SecondaryInstance || !IsIdentityKey(key)) return true;
        int v = value;
        InstanceIdentityStore.Set(key, v.ToString());
        return false;
    }

    // (3) Force a synthetic Steam identity. setSTEAMPlatformData runs in Certification.Start();
    //     a postfix overwrites SteamID/SteamSessionTicket (private setters) via Traverse.
    [HarmonyPatch(typeof(Certification), "setSTEAMPlatformData")]
    [HarmonyPostfix]
    public static void ForceSteamIdentity()
    {
        if (!SvSimConfig.SecondaryInstance) return;
        Traverse.Create(typeof(Certification)).Property("SteamID").SetValue(SvSimConfig.FakeSteamId);
        Traverse.Create(typeof(Certification)).Property("SteamSessionTicket").SetValue(SvSimConfig.FakeTicket);
        Plugin.Log.LogWarning(
            $"Multi-instance: forced SteamID={SvSimConfig.FakeSteamId}, ticket={SvSimConfig.FakeTicket}.");
    }
}

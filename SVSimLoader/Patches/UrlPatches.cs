using System;
using Cute;
using HarmonyLib;

namespace SVSimLoader.Patches;

[HarmonyPatch]
public static class UrlPatches
{
    [HarmonyPatch(typeof(CustomPreference), nameof(CustomPreference.GetApplicationServerURL))]
    [HarmonyPrefix]
    public static bool GetApplicationServerURL(ref string __result)
    {
        __result = SvSimConfig.ApplicationUrl;
        return false;
    }

    /// <summary>
    /// Redirects the deck-builder server (shadowverse-portal.com in prod) to our app server so
    /// the deck-code mint/resolve pair lands on the emulator's <c>DeckBuilderController</c>.
    /// Both client tasks (<c>GenerateDeckCodeTask</c>, <c>GetDeckDataFromCodeTask</c>) build
    /// their URL as <c>GetDeckBuilderServerURL() + ApiList[type]</c> where the right-hand side
    /// is the bare <c>deck_code?format=msgpack</c> / <c>deck?format=msgpack</c> path — matches
    /// our controller routes once this prefix returns the local server's base URL.
    /// </summary>
    [HarmonyPatch(typeof(CustomPreference), nameof(CustomPreference.GetDeckBuilderServerURL))]
    [HarmonyPrefix]
    public static bool GetDeckBuilderServerURL(ref string __result)
    {
        __result = SvSimConfig.ApplicationUrl;
        return false;
    }

    /// <summary>
    /// Redirects the asset CDN ("resource server" — #3 of the 4-server topology, shadowverse.
    /// akamaized.net in prod) to a configured URL, typically a local SVSim.ContentServer. The
    /// stock client composes manifest / asset-bundle / sound / movie URLs as
    /// <c>GetResourceServerURL() + "dl/Manifest/{resVer}/{lang}/{plat}/..."</c> (and similar
    /// for Resource/, Sound/), so this prefix needs to return the bare host root WITH a
    /// trailing slash. Stock <c>GetResourceServerURL</c> returns <c>GetCDNScheme() +
    /// _resourceServerUrl</c> — we sidestep both the scheme accessor and the stored host by
    /// supplying the full URL ourselves.
    /// <para>
    /// Default value mirrors prod so this patch is functionally a no-op until the user opts
    /// in by changing the BepInEx config. To point at a local content server populated by
    /// <c>data_dumps/scripts/content_cdn_mirror.py</c>, set
    /// <c>Connection.ResourceUrl=http://localhost:5149/</c>.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(CustomPreference), nameof(CustomPreference.GetResourceServerURL))]
    [HarmonyPrefix]
    public static bool GetResourceServerURL(ref string __result)
    {
        __result = SvSimConfig.ResourceUrl;
        return false;
    }
}
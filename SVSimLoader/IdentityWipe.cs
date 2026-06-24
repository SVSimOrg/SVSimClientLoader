extern alias game;

using ObscuredPrefs = CodeStage.AntiCheat.ObscuredTypes.ObscuredPrefs;
using PlayerPrefs = game::UnityEngine.PlayerPrefs;

namespace SVSimLoader;

/// <summary>
/// Narrow identity reset for the local-server test loop. Deletes ONLY the three
/// account-keyed entries in <c>Toolbox.SavedataManager</c>:
/// <list type="bullet">
///   <item>UDID — client-generated GUID identifying this install</item>
///   <item>VIEWER_ID — server-assigned viewer id</item>
///   <item>SHORT_UDID — server-assigned short id</item>
/// </list>
/// <para>
/// Calling <c>PlayerPrefs.DeleteAll()</c> would also wipe RES_VER (the asset manifest
/// version that drives the Akamai CDN path), language/sound prefs, and — most importantly
/// for the local-server loop — whatever cache-index metadata the asset layer stores in
/// PlayerPrefs. That made every nuked launch trigger the 15.8 MB tutorial-asset download
/// prompt followed by the 497 MB background-download prompt, even when the on-disk asset
/// bundles were already there. Narrowing to the three identity keys keeps the asset cache
/// in sync with what prod last served, so a wiped client behaves like a fresh signup
/// against the same RES_VER prod is currently on.
/// </para>
/// <para>
/// <c>ObscuredPrefs.DeleteKey</c> deletes both the obscured-key entry
/// (<c>PlayerPrefs[EncryptKey("UDID")]</c>) and the plain-key entry
/// (<c>PlayerPrefs["UDID"]</c>) for each key, matching how
/// <c>Cute/Certification.InitializeFileds</c> would clear them in the game itself.
/// </para>
/// </summary>
public static class IdentityWipe
{
    private static readonly string[] IdentityKeys = { "UDID", "VIEWER_ID", "SHORT_UDID" };

    public static void Execute()
    {
        foreach (var key in IdentityKeys)
        {
            ObscuredPrefs.DeleteKey(key);
        }
        PlayerPrefs.Save();
    }
}

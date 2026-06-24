extern alias game;

using System;
using System.IO;
using BepInEx.Logging;
using Application = game::UnityEngine.Application;

namespace SVSimLoader;

/// <summary>
/// Wipes the locally-cached CDN asset state under <c>Application.persistentDataPath</c>
/// (Windows: <c>%USERPROFILE%\AppData\LocalLow\Cygames\Shadowverse\</c>). Deletes:
/// <list type="bullet">
///   <item><c>manifest.db</c> — the SQLite install-state KV (asset name → installed MD5).
///     This is the load-bearing target; with it gone, <c>AssetHandle.dataHash</c> reads
///     empty for every row, <c>isReDownloadAsset</c> flips true everywhere, and the title
///     screen sees a full pending-download size (per <c>update-flow.md</c> §nuke).</item>
///   <item><c>manifest/</c> — downloaded manifest text files. Refetched at next
///     <c>InitializeManifest</c>.</item>
///   <item><c>a/ b/ s/ v/ m/ f/</c> — actual asset blobs (bundles, BGM, SE, voices, movies,
///     fonts). Deleting these reclaims disk; leaving them would leave orphans the client
///     overwrites on re-download (also fine, just doesn't free space).</item>
/// </list>
/// <para>
/// Does NOT touch:
/// <list type="bullet">
///   <item><c>cardmaster/</c> — API-served, freshness validated by
///     <c>check_time_slip_card_master_hash</c>, separate from the CDN asset system.</item>
///   <item><c>Cookies/</c>, <c>NewReplay/</c>, <c>Player.log</c>, <c>recovery/</c>,
///     <c>*_log/</c> — not asset-related.</item>
///   <item>PlayerPrefs (incl. RES_VER, identity, language) — orthogonal; use
///     <see cref="IdentityWipe"/> for the identity-only subset.</item>
/// </list>
/// </para>
/// <para>
/// Safe to combine with <see cref="IdentityWipe"/> for a full "behaves like a brand-new
/// install" state. Notably, narrowing the asset wipe to this set (rather than a
/// PlayerPrefs.DeleteAll) avoids the cache-index-in-PlayerPrefs footgun that originally
/// motivated narrowing <see cref="IdentityWipe"/> itself — see that class's remarks.
/// </para>
/// </summary>
public static class AssetCacheWipe
{
    private static readonly string[] AssetSubdirs = { "a", "b", "s", "v", "m", "f", "manifest" };

    public static void Execute(ManualLogSource logger)
    {
        var root = Application.persistentDataPath;
        logger.LogInfo($"AssetCacheWipe: root={root}");

        TryDeleteFile(Path.Combine(root, "manifest.db"), logger);

        foreach (var name in AssetSubdirs)
        {
            TryDeleteDirectory(Path.Combine(root, name), logger);
        }
    }

    private static void TryDeleteFile(string path, ManualLogSource logger)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                logger.LogInfo($"  deleted file {path}");
            }
            else
            {
                logger.LogInfo($"  (skip — no file at {path})");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning($"  failed to delete {path}: {ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string path, ManualLogSource logger)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                logger.LogInfo($"  deleted dir {path}");
            }
            else
            {
                logger.LogInfo($"  (skip — no dir at {path})");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning($"  failed to delete {path}: {ex.Message}");
        }
    }
}

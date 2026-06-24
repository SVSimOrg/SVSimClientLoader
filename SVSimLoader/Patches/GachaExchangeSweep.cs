using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using Cute;
using LitJson;
using UnityEngine;
using Wizard;
using Wizard.Scripts.Network.Data.TaskData.SpotCardExchange;

namespace SVSimLoader.Patches;

/// <summary>
/// Replaces the former LeaderSkinPoolSweep. Fires GachaPointExchangeInfoTask
/// (/pack/get_gacha_point_rewards) for every pack id in
/// Wizard.Data.Master.CardSetNameMgr.GetList() — i.e. the full client master
/// list (~279 ids), not just the 35 in-catalog packs /pack/info returns.
///
/// Goal: capture per-pack tradeable card_id lists for off-catalog families
/// (Throwback 80xxx, Rotation Select 97/98xxx, Premium 93xxx, anniversary
/// 92xxx/95xxx) so the drawrates parser's tier-4 disambiguation can resolve
/// the residual ambiguous joins. See
/// docs/superpowers/specs/2026-05-30-gacha-exchange-sweep-design.md.
///
/// Trigger: first /pack/info response of the session (same as the old sweep).
/// Capture path: responses ride the existing ExaminationPatches.SetResponseData
/// EnableTrafficCapture branch into traffic.ndjson — this sweep never calls
/// CaptureWriter directly.
///
/// Misses (result_code != 1) are recorded in a persistent ledger at
/// BepInEx/svsim-captures/gacha-sweep-misses.json so re-runs across sessions
/// don't re-hit dead ids.
///
/// Gated by SvSimConfig.SweepGachaExchange (off by default). Pacing from
/// SvSimConfig.GachaExchangeSweepPacingSeconds (default 0.5, clamped >= 0.1).
/// Smoke-test mode via SvSimConfig.SweepDryRunIds (comma-separated allowlist).
/// </summary>
internal static class GachaExchangeSweep
{
    private static bool _sweepStarted;
    private static readonly object _lock = new object();

    private const string LedgerFileName = "gacha-sweep-misses.json";
    private const string LedgerSubdir = "svsim-captures";

    public static void OnPackInfoResponse(JsonData _)
    {
        lock (_lock)
        {
            if (_sweepStarted) return;
            _sweepStarted = true;
        }

        if (Plugin.Instance == null)
        {
            Plugin.Log.LogError("GachaExchangeSweep: Plugin.Instance is null — cannot start coroutine.");
            return;
        }

        var ids = BuildIdList();
        if (ids.Count == 0)
        {
            Plugin.Log.LogWarning("GachaExchangeSweep: BuildIdList returned 0 ids — nothing to sweep.");
            return;
        }

        float pacing = Mathf.Max(0.1f, SvSimConfig.GachaExchangeSweepPacingSeconds);
        Plugin.Log.LogInfo($"GachaExchangeSweep: queued {ids.Count} ids (pacing={pacing}s).");
        Plugin.Instance.StartCoroutine(SweepCoroutine(ids, pacing));
    }

    /// <summary>
    /// Builds the candidate id list: every numeric CardSetName.ID from
    /// CardSetNameMgr, minus the persistent miss ledger, optionally
    /// intersected with SweepDryRunIds.
    /// </summary>
    private static List<int> BuildIdList()
    {
        var all = new HashSet<int>();
        var master = Data.Master?.CardSetNameMgr;
        if (master == null)
        {
            Plugin.Log.LogWarning("GachaExchangeSweep: Wizard.Data.Master.CardSetNameMgr is null — master data not loaded yet?");
            return new List<int>();
        }
        var list = master.GetList();
        if (list == null)
        {
            Plugin.Log.LogWarning("GachaExchangeSweep: CardSetNameMgr.GetList() returned null.");
            return new List<int>();
        }
        foreach (var cs in list)
        {
            if (cs == null || string.IsNullOrEmpty(cs.ID)) continue;
            if (int.TryParse(cs.ID, out int id)) all.Add(id);
        }

        var misses = LoadMissLedger();
        all.ExceptWith(misses);

        var dryRun = ParseDryRunIds(SvSimConfig.SweepDryRunIds);
        if (dryRun.Count > 0)
        {
            all.IntersectWith(dryRun);
            Plugin.Log.LogInfo($"GachaExchangeSweep: SweepDryRunIds active — restricted to {all.Count} of {dryRun.Count} requested ids.");
        }

        var ordered = new List<int>(all);
        ordered.Sort();
        return ordered;
    }

    private static HashSet<int> ParseDryRunIds(string raw)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrEmpty(raw)) return set;
        foreach (var part in raw.Split(','))
        {
            var token = part.Trim();
            if (token.Length == 0) continue;
            if (int.TryParse(token, out int id)) set.Add(id);
            else Plugin.Log.LogWarning($"GachaExchangeSweep: SweepDryRunIds token '{token}' is not an int — skipped.");
        }
        return set;
    }

    private static IEnumerator SweepCoroutine(List<int> ids, float pacing)
    {
        int ok = 0, fail = 0;
        var newMisses = new HashSet<int>();
        for (int i = 0; i < ids.Count; i++)
        {
            int id = ids[i];
            // odds_gacha_id and parent_gacha_id observed equal in the natural-flow capture
            // (data_dumps/captures/traffic_prod_tradeables_capture.ndjson). Pass the same value twice.
            var task = new GachaPointExchangeInfoTask();
            task.SetParameter(id, id);
            Plugin.Log.LogInfo($"GachaExchangeSweep: [{i + 1}/{ids.Count}] pack_id={id}");
            yield return Toolbox.NetworkManager.Connect(task, _ => { });
            if (task.isServerResultCodeOK())
            {
                ok++;
            }
            else
            {
                fail++;
                newMisses.Add(id);
                Plugin.Log.LogWarning($"GachaExchangeSweep: pack_id={id} returned result_code={task.GetResultCode()} — recording as miss.");
            }
            yield return new WaitForSeconds(pacing);
        }

        int totalLedger = -1;
        if (newMisses.Count > 0)
        {
            try
            {
                totalLedger = SaveMissLedger(newMisses);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"GachaExchangeSweep: SaveMissLedger failed: {e}");
            }
        }
        if (totalLedger >= 0)
        {
            Plugin.Log.LogInfo($"GachaExchangeSweep: complete. ok={ok} fail={fail}/{ids.Count}, ledger now {totalLedger} ids.");
        }
        else
        {
            Plugin.Log.LogInfo($"GachaExchangeSweep: complete. ok={ok} fail={fail}/{ids.Count}, ledger unchanged.");
        }
    }

    private static string LedgerPath()
    {
        return Path.Combine(Paths.BepInExRootPath, LedgerSubdir, LedgerFileName);
    }

    private static HashSet<int> LoadMissLedger()
    {
        var result = new HashSet<int>();
        var path = LedgerPath();
        if (!File.Exists(path)) return result;
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrEmpty(json)) return result;
            var data = JsonMapper.ToObject(json);
            if (data == null || !data.IsObject || !data.Keys.Contains("miss_ids")) return result;
            var arr = data["miss_ids"];
            if (arr == null || !arr.IsArray) return result;
            for (int i = 0; i < arr.Count; i++)
            {
                var v = arr[i];
                if (v == null) continue;
                if (v.IsInt) result.Add((int)v);
                else if (v.IsLong) result.Add((int)(long)v);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"GachaExchangeSweep: LoadMissLedger failed (treating as empty): {e}");
            return new HashSet<int>();
        }
        return result;
    }

    /// <summary>
    /// Atomic union-merge save. Reads the existing ledger, unions the new
    /// misses in, writes to a temp file, then atomically replaces the dest.
    /// Returns the total miss-id count after merge.
    /// </summary>
    private static int SaveMissLedger(HashSet<int> newMisses)
    {
        var path = LedgerPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var merged = LoadMissLedger();
        merged.UnionWith(newMisses);

        var ordered = new List<int>(merged);
        ordered.Sort();

        var payload = new Dictionary<string, object>
        {
            { "miss_ids", ordered },
            { "last_updated", DateTime.UtcNow.ToString("o") }
        };
        var json = JsonMapper.ToJson(payload);

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null);
        }
        else
        {
            File.Move(tempPath, path);
        }
        return merged.Count;
    }
}

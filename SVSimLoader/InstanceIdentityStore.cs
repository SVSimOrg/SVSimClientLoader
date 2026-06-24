using System.Collections.Generic;
using System.IO;

namespace SVSimLoader
{
    /// <summary>
    /// File-backed store for the three account-identity PlayerPrefs keys (UDID, VIEWER_ID,
    /// SHORT_UDID), so a second client instance on the same machine gets its own identity
    /// instead of sharing the per-Windows-user registry store. Only these three keys are
    /// redirected; everything else (RES_VER, language, asset cache) stays in the shared store.
    /// Persisted as dependency-free key=value lines — the values are a GUID and two integers,
    /// which never contain '=' or newlines.
    /// </summary>
    public static class InstanceIdentityStore
    {
        public static readonly string[] Keys = { "UDID", "VIEWER_ID", "SHORT_UDID" };

        private static string _path;
        private static readonly Dictionary<string, string> _values = new Dictionary<string, string>();

        public static void Initialize(string path)
        {
            _path = path;
            _values.Clear();
            if (!File.Exists(_path)) return;
            foreach (var line in File.ReadAllLines(_path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq);
                string val = line.Substring(eq + 1);
                if (System.Array.IndexOf(Keys, key) >= 0) _values[key] = val;
            }
        }

        public static bool TryGet(string key, out string value) => _values.TryGetValue(key, out value);

        public static void Set(string key, string value)
        {
            _values[key] = value;
            Save();
        }

        private static void Save()
        {
            var lines = new List<string>();
            foreach (var kv in _values) lines.Add(kv.Key + "=" + kv.Value);
            File.WriteAllLines(_path, lines.ToArray());
        }
    }
}

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Rhino.AI;

// Live catalog of the models this machine's pi installation can actually use, so Pi's model list
// is neither hand-maintained in Definitions.json nor padded with models whose providers have no
// credentials. The authoritative "available" set comes from `pi --list-models`: that command and
// the TUI's /model picker both read ModelRuntime.getAvailable(), which keeps only models whose
// provider resolves auth (env vars, auth.json, apiKey in models.json, OAuth). We parse its table
// output, then join against the on-disk catalogs for display names:
//   models-store.json  - provider catalogs pi fetched upstream: { "<provider>": { "models": [...] } }
//   models.json        - user-defined providers that override the store on conflict:
//                        { "providers": { "<provider>": { "models": [...] } } }
// If the CLI cannot be run (missing, slow, changed output) we fall back to the full file catalog
// rather than an empty list. The result is cached and refreshed when a config file's last-write
// time changes or the TTL elapses, so UI access stays cheap between panel opens.
internal static class PiModelCatalog
{
    private static readonly object Gate = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private static readonly int ListModelsTimeoutMs = 30_000;

    private static IReadOnlyList<ModelSpec>? _cache;
    private static long _storeStamp = -1;
    private static long _userStamp = -1;
    private static long _authStamp = -1;
    private static DateTime _cachedAtUtc;

    public static IReadOnlyList<ModelSpec> Load(string? piExe)
    {
        lock (Gate)
        {
            string dir = AgentDir();
            long storeStamp = Mtime(Path.Combine(dir, "models-store.json"));
            long userStamp = Mtime(Path.Combine(dir, "models.json"));
            long authStamp = Mtime(Path.Combine(dir, "auth.json"));

            if (_cache is not null &&
                _storeStamp == storeStamp && _userStamp == userStamp && _authStamp == authStamp &&
                DateTime.UtcNow - _cachedAtUtc < Ttl)
                return _cache;

            List<ModelSpec> fileCatalog = BuildFileCatalog(Path.Combine(dir, "models-store.json"), Path.Combine(dir, "models.json"));
            List<string>? available = piExe is not null ? RunListModels(piExe) : null;

            IReadOnlyList<ModelSpec> result;
            if (available is null)
            {
                // CLI unavailable or unreadable output: show everything configured on disk rather
                // than nothing - a stale-ish list beats an empty dropdown.
                result = fileCatalog;
            }
            else
            {
                Dictionary<string, ModelSpec> byId = new();
                foreach (ModelSpec spec in fileCatalog)
                    byId[spec.Id] = spec;

                // Keep the CLI's ordering (provider, then id - the same sort /model shows).
                List<ModelSpec> filtered = new(available.Count);
                foreach (string qualified in available)
                    filtered.Add(byId.TryGetValue(qualified, out ModelSpec spec) ? spec : new ModelSpec(qualified, qualified));
                result = filtered;
            }

            _cache = result;
            _storeStamp = storeStamp;
            _userStamp = userStamp;
            _authStamp = authStamp;
            _cachedAtUtc = DateTime.UtcNow;
            return _cache;
        }
    }

    // Runs `pi --list-models` and returns the qualified "provider/model" ids in output order.
    // null means "could not determine" (spawn/timeout/parse failure); an empty list is a real
    // answer meaning no provider has credentials right now.
    private static List<string>? RunListModels(string piExe)
    {
        ProcessStartInfo psi = new()
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        CliProcess.ConfigureEncoding(psi);
        CliProcess.ConfigureFileName(psi, piExe);
        psi.AddArgument("--list-models");

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("process did not start");
        }
        catch (Exception)
        {
            return null;
        }

        string stdout;
        try
        {
            using (proc)
            {
                Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
                Task<string> errTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(ListModelsTimeoutMs))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    return null;
                }
                stdout = outTask.GetAwaiter().GetResult();
            }
        }
        catch (Exception)
        {
            return null;
        }

        List<string> models = [];
        bool headerSeen = false;
        foreach (string line in stdout.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.Length == 0)
                continue;
            // Columns are padded to their width plus two spaces, so a run of >=2 spaces is the
            // only separator; model ids contain no spaces (pi's own --model references rely on it).
            string[] cols = Regex.Split(trimmed, @" {2,}");
            if (cols.Length < 3)
                continue;
            if (!headerSeen)
            {
                if (cols[0] != "provider" || cols[1] != "model")
                    return null; // unexpected output shape - don't trust a partial parse
                headerSeen = true;
                continue;
            }
            models.Add($"{cols[0]}/{cols[1]}");
        }
        return headerSeen ? models : null;
    }

    private static List<ModelSpec> BuildFileCatalog(string storePath, string userPath)
    {
        List<ModelSpec> models = [];
        Dictionary<string, int> index = new(); // qualified id -> position in models
        Merge(storePath, index, models);
        Merge(userPath, index, models); // user config wins on conflict
        return models;
    }

    private static string AgentDir()
    {
        string? dir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        return dir is { Length: > 0 } ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent");
    }

    // -1 when the file does not exist, so a missing file is a stable stamp, not an error.
    private static long Mtime(string path)
    {
        try { return new FileInfo(path).LastWriteTimeUtc.Ticks; }
        catch (Exception) { return -1; }
    }

    private static void Merge(string path, Dictionary<string, int> index, List<ModelSpec> models)
    {
        if (Mtime(path) < 0)
            return;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return; // unreadable or half-written file: skip it, keep whatever we have
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return;

        // models.json wraps providers in a top-level "providers" object; models-store.json does not.
        JsonElement providers = doc.RootElement.TryGetProperty("providers", out JsonElement p) && p.ValueKind == JsonValueKind.Object
            ? p
            : doc.RootElement;

        foreach (JsonProperty providerProp in providers.EnumerateObject())
        {
            if (providerProp.Value.ValueKind != JsonValueKind.Object ||
                !providerProp.Value.TryGetProperty("models", out JsonElement modelsEl) ||
                modelsEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement modelEl in modelsEl.EnumerateArray())
            {
                if (modelEl.ValueKind != JsonValueKind.Object ||
                    !TryStr(modelEl, "id", out string id) ||
                    id.Length == 0)
                    continue;

                string qualified = $"{providerProp.Name}/{id}";
                ModelSpec spec = new(TryStr(modelEl, "name", out string name) ? name : id, qualified);
                if (index.TryGetValue(qualified, out int i))
                    models[i] = spec; // override (user config replacing the store entry)
                else
                {
                    index[qualified] = models.Count;
                    models.Add(spec);
                }
            }
        }
    }

    private static bool TryStr(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
        {
            value = s;
            return true;
        }
        value = string.Empty;
        return false;
    }
}

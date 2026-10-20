// Slim managed-tool fetcher — Sparta Clips only needs ONE: the BtbN FFmpeg GPL build used for AV1 encoding,
// keyframe probing, proxies, trims and share renders. Downloaded on first use to %APPDATA%\SpartaClips\tools\ffmpeg
// and run as a separate CLI process (not bundled/redistributed → no GPL entanglement). This is the trimmed
// counterpart of Sparta Dock's Services/Games/ManagedTools.cs: same class/method names so Av1Transcoder compiles
// unchanged, but with the emulator descriptors + pad-arbitration (SonyPad/RemotePlay/SessionState) removed.
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SpartaClips
{
    internal sealed class ManagedTool
    {
        public string Key = "", Title = "", Repo = "", ExeName = "";
        public Regex AssetRx = new("$^");
        public string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpartaClips", "tools", Key);
    }

    internal static class ManagedTools
    {
        private static readonly ManagedTool[] Reg =
        {
            new() { Key = "ffmpeg", Title = "AV1 Encoder (FFmpeg)", Repo = "BtbN/FFmpeg-Builds", ExeName = "ffmpeg.exe",
                    AssetRx = new(@"ffmpeg-master-latest-win64-gpl\.zip$", RegexOptions.IgnoreCase) },
        };

        public static ManagedTool? Get(string key) =>
            Reg.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

        /// <summary>Full path to the tool's exe (recursive — BtbN nests it under bin/), or null when not installed.
        /// Cached after the first hit — this backs FfmpegReady/FfmpegPath which are called several times per ffmpeg
        /// operation, and a recursive directory walk per call was a measurable chunk of gallery-load latency.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _exeCache = new();
        public static string? ExePath(ManagedTool t)
        {
            try
            {
                if (_exeCache.TryGetValue(t.Key, out var hit)) { if (File.Exists(hit)) return hit; _exeCache.TryRemove(t.Key, out _); }
                if (!Directory.Exists(t.Root)) return null;
                var found = Directory.GetFiles(t.Root, t.ExeName, SearchOption.AllDirectories).FirstOrDefault();
                if (found != null) _exeCache[t.Key] = found;
                return found;
            }
            catch { return null; }
        }
        internal static void InvalidateExeCache(ManagedTool t) => _exeCache.TryRemove(t.Key, out _);

        public static string InstalledVersion(ManagedTool t)
        {
            try { var p = Path.Combine(t.Root, "version.txt"); return File.Exists(p) ? File.ReadAllText(p).Trim() : ""; }
            catch { return ""; }
        }

        private static HttpClient NewHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SpartaClips");   // GitHub API requires a UA
            return http;
        }

        /// <summary>Latest release's (tag, matching-zip url), or null when unreachable/no matching asset.</summary>
        public static async Task<(string tag, string url)?> LatestAsync(ManagedTool t)
        {
            try
            {
                using var http = NewHttp();
                var json = await http.GetStringAsync($"https://api.github.com/repos/{t.Repo}/releases/latest");
                var o = JObject.Parse(json);
                string tag = (string?)o["tag_name"] ?? "";
                var asset = (o["assets"] as JArray)?.FirstOrDefault(a => t.AssetRx.IsMatch((string?)a["name"] ?? ""));
                string url = (string?)asset?["browser_download_url"] ?? "";
                return tag.Length > 0 && url.Length > 0 ? (tag, url) : null;
            }
            catch { return null; }
        }

        /// <summary>Download → extract → swap. Progress states: down (with %) / extract / done / fail_download / failed.</summary>
        // Serialized: on a fresh install every gallery card's thumb/info request raced EnsureAsync into N concurrent
        // downloads of the SAME zip path — losers threw on File.Create and their cleanup deleted the winner's staging
        // dir mid-extract (flaky first-run installs). One installer runs; late waiters see it's ready and return.
        private static readonly System.Threading.SemaphoreSlim _installGate = new(1, 1);
        public static async Task DownloadAndInstallAsync(ManagedTool t, Action<string, int, string> progress)
        {
            string staging = t.Root + ".new";
            string zip = t.Root + ".zip";
            await _installGate.WaitAsync();
            try
            {
                if (ExePath(t) != null) { progress("done", 100, "installed"); return; }   // a concurrent caller finished while we waited
                var latest = await LatestAsync(t);
                if (latest == null) { progress("fail_download", 0, "couldn't reach the release feed"); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(t.Root)!);

                using (var http = NewHttp())
                using (var resp = await http.GetAsync(latest.Value.url, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!resp.IsSuccessStatusCode) { progress("fail_download", 0, $"server said {(int)resp.StatusCode}"); return; }
                    long total = resp.Content.Headers.ContentLength ?? 0;
                    progress("down", 0, "starting…");
                    await using var src = await resp.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(zip);
                    var buf = new byte[81920];
                    long got = 0; int lastPct = -10;
                    for (int n; (n = await src.ReadAsync(buf)) > 0; )
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n));
                        got += n;
                        if (total > 0)
                        {
                            int pct = (int)(got * 100 / total);
                            if (pct >= lastPct + 5) { lastPct = pct; progress("down", pct, $"{pct}%"); }
                        }
                    }
                }

                progress("extract", 100, "downloaded — installing…");
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, staging, overwriteFiles: true);
                if (!Directory.GetFiles(staging, t.ExeName, SearchOption.AllDirectories).Any())
                { progress("failed", 0, "program exe missing from the package"); return; }

                KillRunning(t);
                try { if (Directory.Exists(t.Root)) Directory.Delete(t.Root, true); } catch { }
                Directory.Move(staging, t.Root);
                InvalidateExeCache(t);   // the cached exe path points into the replaced tree
                File.WriteAllText(Path.Combine(t.Root, "version.txt"), latest.Value.tag);
                progress("done", 100, latest.Value.tag);
            }
            catch (HttpRequestException ex) { progress("fail_download", 0, ex.Message); }
            catch (Exception ex) { progress("failed", 0, ex.Message); }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                _installGate.Release();
            }
        }

        private static void KillRunning(ManagedTool t)
        {
            try
            {
                string proc = Path.GetFileNameWithoutExtension(t.ExeName);
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(proc))
                {
                    try
                    {
                        // Only kill OUR managed copy (path under t.Root) — before: every process named "ffmpeg" on the
                        // machine died, including the user's own jobs and our in-flight proxy/AV1 transcodes.
                        string? path = null; try { path = p.MainModule?.FileName; } catch { }   // access denied (elevated/other user) → not ours
                        if (path == null || !path.StartsWith(t.Root, StringComparison.OrdinalIgnoreCase)) continue;
                        p.Kill(); p.WaitForExit(3000);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }
    }
}

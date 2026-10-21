// Self-update: checks the newest GitHub Release of kayliox/sparta-clips (every push to main publishes one — see
// .github/workflows/release.yml), and when it is newer than this build, TrayCore shows UpdatePrompt so the user decides.
// "Install" downloads SpartaClipsSetup.exe, verifies it (exact size + the SHA-256 digest GitHub publishes per asset),
// starts it silently with /relaunch=..., and quits so the installer can replace the files; the installer reopens the app.
//
// Only an INSTALLED copy updates itself (the Inno uninstall key's InstallLocation is this folder). A portable/dev copy
// (e.g. a deploy.ps1 build) never auto-checks — installing would create a second, separate copy beside it.
// State that only the native side writes (skipped version, remind-later time) lives in update.json, NOT config.json:
// the web UI rewrites config.json wholesale on every save and would clobber it.
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace SpartaClips
{
    internal sealed record ReleaseInfo(Version Version, string Notes, string DownloadUrl, long Size, string? Sha256, string PageUrl);

    internal static class AppUpdater
    {
        private const string Repo = "kayliox/sparta-clips";
        private const string AssetName = "SpartaClipsSetup.exe";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F3C2A1E-5B7D-4E9A-9C2F-8D1B4A7E3C55}_is1";   // = the installer's AppId

        private static readonly HttpClient Http = MakeHttp();
        private static HttpClient MakeHttp()
        {
            var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // per-request timeouts via CancellationToken
            h.DefaultRequestHeaders.UserAgent.ParseAdd("SpartaClips/" + VersionText(Current));   // GitHub's API requires a UA
            h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return h;
        }

        /// <summary>This build's version (installer builds are 0.1.&lt;build number&gt;; a local build is 0.1.0).</summary>
        public static Version Current
        {
            get
            {
                var v = typeof(AppUpdater).Assembly.GetName().Version ?? new Version(0, 1, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }
        public static string VersionText(Version v) => v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);

        /// <summary>True when this exe runs from the folder the installer put it in (so updating it makes sense).</summary>
        public static bool IsInstalledCopy
        {
            get
            {
                try
                {
                    string here = Norm(AppContext.BaseDirectory);
                    foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                    {
                        using var k = root.OpenSubKey(UninstallKey);
                        if (k?.GetValue("InstallLocation") is string loc && Norm(loc) == here) return true;
                    }
                }
                catch { }
                return false;
            }
        }
        private static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant();

        // ── update.json (native-only state) ──
        private static string StatePath => Path.Combine(AppConfig.Dir, "update.json");
        private static JObject LoadState() { try { return File.Exists(StatePath) ? JObject.Parse(File.ReadAllText(StatePath)) : new JObject(); } catch { return new JObject(); } }
        private static void SaveState(JObject s) { try { Directory.CreateDirectory(AppConfig.Dir); File.WriteAllText(StatePath, s.ToString()); } catch { } }
        public static void Skip(Version v) { var s = LoadState(); s["skipped"] = VersionText(v); SaveState(s); }
        public static void RemindLater(TimeSpan after) { var s = LoadState(); s["remindAfterUtc"] = DateTime.UtcNow.Add(after).ToString("o"); SaveState(s); }
        /// <summary>Whether an automatic check should offer this release (not skipped, not snoozed).</summary>
        public static bool ShouldOffer(ReleaseInfo r)
        {
            var s = LoadState();
            if ((string?)s["skipped"] == VersionText(r.Version)) return false;
            if (DateTime.TryParse((string?)s["remindAfterUtc"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) && DateTime.UtcNow < t) return false;
            return true;
        }

        /// <summary>The newest published release, or null (offline, rate-limited, no installer asset...).</summary>
        public static async Task<ReleaseInfo?> FetchLatestAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                string json = await Http.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases/latest", cts.Token);
                var o = JObject.Parse(json);
                var m = Regex.Match((string?)o["tag_name"] ?? "", @"^v?(\d+)\.(\d+)\.(\d+)$");
                if (!m.Success) return null;
                var ver = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                foreach (var a in (o["assets"] as JArray) ?? new JArray())
                {
                    if ((string?)a["name"] != AssetName) continue;
                    string url = (string?)a["browser_download_url"] ?? "";
                    if (!url.StartsWith("https://github.com/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase)) return null;
                    string? digest = (string?)a["digest"];   // "sha256:<hex>" (published by GitHub for every uploaded asset)
                    string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                    return new ReleaseInfo(ver, CleanNotes((string?)o["body"] ?? ""), url, (long?)a["size"] ?? 0, sha, (string?)o["html_url"] ?? "");
                }
            }
            catch (Exception ex) { try { DiagLog.Line("UPDATE", "check failed: " + ex.Message); } catch { } }
            return null;
        }

        // Release bodies look like "## What's new\n- item\n- item\n\n---\n**Download ...**" — keep just the item list.
        private static string CleanNotes(string body)
        {
            body = body.Replace("\r\n", "\n");
            int a = body.IndexOf("## What's new", StringComparison.OrdinalIgnoreCase);
            if (a >= 0) body = body[(body.IndexOf('\n', a) is int nl && nl > 0 ? nl + 1 : body.Length)..];
            else if ((a = body.IndexOf("Changes in this build:", StringComparison.OrdinalIgnoreCase)) >= 0)   // releases before the notes format
                body = "- " + body[(a + "Changes in this build:".Length)..].Trim().Split('\n')[0];   // just the commit's subject line
            int b = body.IndexOf("\n---", StringComparison.Ordinal);
            if (b >= 0) body = body[..b];
            var sb = new System.Text.StringBuilder();
            foreach (var raw in body.Split('\n'))
            {
                string l = raw.Trim(); if (l.Length == 0) continue;
                if (l.StartsWith("- ") || l.StartsWith("* ")) l = "•  " + l[2..];
                l = l.Replace("**", "").Replace("`", "");
                sb.Append(l).Append('\n');
            }
            string s = sb.ToString().Trim();
            return s.Length > 1500 ? s[..1500].TrimEnd() + "…" : s;
        }

        private static string DownloadDir => Path.Combine(Path.GetTempPath(), "SpartaClipsUpdate");
        /// <summary>Startup: remove a downloaded installer left from a previous update (~55 MB in %TEMP%).</summary>
        public static void CleanupDownloads() { try { if (Directory.Exists(DownloadDir)) Directory.Delete(DownloadDir, true); } catch { } }

        /// <summary>Downloads + verifies the installer; returns its path, or null on any failure/mismatch.</summary>
        public static async Task<string?> DownloadAsync(ReleaseInfo r, IProgress<(long done, long total)> progress, CancellationToken ct)
        {
            string dir = DownloadDir;
            string dst = Path.Combine(dir, "SpartaClipsSetup-" + VersionText(r.Version) + ".exe"), part = dst + ".part";
            try
            {
                Directory.CreateDirectory(dir);
                foreach (var f in Directory.GetFiles(dir)) { try { File.Delete(f); } catch { } }   // old downloads
                using (var resp = await Http.GetAsync(r.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? r.Size;
                    using var src = await resp.Content.ReadAsStreamAsync(ct);
                    using var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                    var buf = new byte[1 << 16]; long done = 0; int n; long lastTick = 0;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, n), ct); done += n;
                        if (Environment.TickCount64 - lastTick > 100) { lastTick = Environment.TickCount64; progress.Report((done, total)); }
                    }
                    progress.Report((done, total));
                }
                var len = new FileInfo(part).Length;
                if (r.Size > 0 && len != r.Size) throw new InvalidDataException($"size {len} != {r.Size}");
                if (r.Sha256 != null)
                {
                    using var fs = File.OpenRead(part);
                    string got = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                    if (got != r.Sha256) throw new InvalidDataException("checksum mismatch");
                }
                File.Move(part, dst, true);
                DiagLog.Line("UPDATE", "downloaded " + VersionText(r.Version) + " (" + len / 1048576 + " MB, " + (r.Sha256 != null ? "sha256 ok" : "no digest") + ")");
                return dst;
            }
            catch (Exception ex)
            {
                try { File.Delete(part); } catch { }
                if (!ct.IsCancellationRequested) try { DiagLog.Line("UPDATE", "download failed: " + ex.Message); } catch { }
                return null;
            }
        }

        /// <summary>Starts the installer silently; it waits for this process to exit, updates, then reopens the app
        /// (with the window when reopenWindow, else to the tray). The caller must quit right after.</summary>
        public static bool LaunchInstaller(string setupPath, bool reopenWindow)
        {
            try
            {
                Process.Start(new ProcessStartInfo(setupPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /relaunch=" + (reopenWindow ? "window" : "tray"))
                { UseShellExecute = true });
                DiagLog.Line("UPDATE", "installer started");
                return true;
            }
            catch (Exception ex) { try { DiagLog.Line("UPDATE", "installer start failed: " + ex.Message); } catch { } return false; }
        }
    }
}

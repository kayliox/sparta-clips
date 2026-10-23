// enginetest — regression tests for the capture engine internals (2026-10-20 fixes), run against the BUILT app DLL.
//
//   A. AV1 encoder pick     — the chosen encoder survives a real encode (av1_nvenc was picked on AMD and failed)
//   B. window bitrate       — a 1440p window is budgeted for 1440p, not the 1080 setting
//   C. per-app game audio   — tone through the default device → ring → mux: level within 1 dB, ANY device volume
//   D. silent game track    — never replaces the clip's audio (returns false, original kept)
//   E. share resolution     — small tiers scale down, the 500 MB (Nitro) tier stays native
//   F. capture-target filter— desktop / null / shell windows are never capture targets
//   G. share mix            — a chat track muted in the editor stays out of the Discord share
//   H. window-switch replay — segments of different sizes are letterboxed + joined (full length, not dropped)
//
// C plays a quiet (-26 dBFS) 440 Hz tone for ~2 s on the default output device. Needs ffmpeg (fetched by the app on
// first run, %APPDATA%\SpartaClips\tools\ffmpeg); C/D are SKIPPED without it.
// Run:  dotnet build ..\..\SpartaClips.csproj -c Release ; dotnet run -c Release
using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

const BindingFlags ANY = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
string dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\bin\x64\Release\net8.0-windows10.0.22621.0\SpartaClips.dll"));
if (!File.Exists(dll)) { Console.WriteLine("Build the app first (Release x64): " + dll); return 2; }
var asm = Assembly.LoadFrom(dll);
Type T(string n) => asm.GetType("SpartaClips." + n, true);
object Call(Type t, string m, params object[] a) => t.GetMethod(m, ANY).Invoke(null, a);
void Set(Type t, string f, object v) => t.GetField(f, ANY).SetValue(null, v);

int fails = 0;
void Check(string name, bool ok, string detail = "") { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  — " + detail : "")); if (!ok) fails++; }
void Skip(string name, string why) => Console.WriteLine("SKIP  " + name + "  — " + why);

var av1 = T("Av1Transcoder"); var rec = T("ClipRecorder"); var ffm = T("ClipFfmpeg"); var share = T("ClipShare");
string ff = (string)av1.GetProperty("FfmpegPath", ANY).GetValue(null);
bool haveFf = ff != null && File.Exists(ff);
string tmp = Path.Combine(Path.GetTempPath(), "sc_enginetest"); Directory.CreateDirectory(tmp);

// A — encoder pick
if (haveFf)
{
    string enc = (string)Call(av1, "PickEncoder", false);
    bool works = enc.Length > 0 && (bool)Call(av1, "EncoderWorks", enc);
    Check("A. AV1 encoder pick", works, "picked '" + enc + "'");
}
else Skip("A. AV1 encoder pick", "no ffmpeg");

// B — native-window bitrate (30 Mbps @ "1080" setting, captured window 1440 tall → 30 × (1440/1080)² ≈ 53)
Set(rec, "_mbps", 30); Set(rec, "_resH", 1080); Set(rec, "_capH", 1440);
int mbps = (int)Call(rec, "EffectiveMbps");
Set(rec, "_capH", 0);
Check("B. window bitrate budget", mbps == 53, mbps + " Mbps (want 53)");

// C — per-app game audio level through ring → dump → mux
if (haveFf)
{
    string mp4 = Path.Combine(tmp, "c.mp4"), wav = Path.Combine(tmp, "c.wav");
    Ffmpeg($"-f lavfi -i testsrc2=s=320x240:r=30:d=6 -f lavfi -i anullsrc=r=48000:cl=stereo -t 6 -c:v libx264 -c:a aac -shortest \"{mp4}\"");
    Func<IWaveIn> factory = () => (IWaveIn)Activator.CreateInstance(T("GameAudioCapture"), ANY, null, new object[] { Environment.ProcessId, "test" }, null);
    var side = Activator.CreateInstance(T("AudioSidecar"), ANY, null, new object[] { factory, "game" }, null);
    side.GetType().GetMethod("StartRing").Invoke(side, new object[] { 30 });
    using var dev = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    float vol = dev.AudioEndpointVolume.MasterVolumeLevelScalar;
    Thread.Sleep(400);
    var sig = new SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.05 };   // -26.0 dBFS peak
    using (var wo = new WasapiOut(dev, AudioClientShareMode.Shared, false, 100)) { wo.Init(sig.ToWaveProvider()); wo.Play(); Thread.Sleep(2000); wo.Stop(); }
    Thread.Sleep(300);
    side.GetType().GetMethod("DumpRing").Invoke(side, new object[] { wav, 6 });
    side.GetType().GetMethod("StopRing").Invoke(side, null);
    bool muxed = ((Task<bool>)Call(ffm, "ReplaceAudioAsync", mp4, wav, true)).GetAwaiter().GetResult();
    double peak = MaxVolume(mp4);
    Check("C. per-app game audio level", muxed && Math.Abs(peak - -26.0) <= 1.0,
          $"muxed={muxed} peak={peak:F1} dB (want -26.0 ±1) · device '{dev.FriendlyName}' {dev.AudioClient.MixFormat.Channels}ch at {vol * 100:F0}% volume");

    // D — a silent game track must not replace the clip's audio
    string loud = Path.Combine(tmp, "d.mp4"), silent = Path.Combine(tmp, "d.wav");
    Ffmpeg($"-f lavfi -i testsrc2=s=320x240:r=30:d=4 -f lavfi -i sine=frequency=1000:sample_rate=48000 -t 4 -c:v libx264 -c:a aac -shortest \"{loud}\"");
    Ffmpeg($"-f lavfi -i anullsrc=r=48000:cl=stereo -t 4 -c:a pcm_s16le \"{silent}\"");
    double before = MaxVolume(loud);
    bool replaced = ((Task<bool>)Call(ffm, "ReplaceAudioAsync", loud, silent, false)).GetAwaiter().GetResult();
    Check("D. silent game track keeps original", !replaced && Math.Abs(MaxVolume(loud) - before) < 0.5, $"replaced={replaced}");
}
else { Skip("C. per-app game audio level", "no ffmpeg"); Skip("D. silent game track keeps original", "no ffmpeg"); }

// G — the Discord share honors the editor's chat mute (before: mic + chat were ALWAYS mixed in at 100%)
if (haveFf)
{
    string dir = Path.Combine(tmp, "clips"); Directory.CreateDirectory(dir);
    Call(rec, "SetClipsDir", dir);   // this process only — the running app's clips folder is untouched
    string clip = Path.Combine(dir, "g.mp4");
    Ffmpeg($"-f lavfi -i testsrc2=s=320x240:r=30:d=4 -f lavfi -i anullsrc=r=48000:cl=stereo -t 4 -c:v libx264 -c:a aac -shortest \"{clip}\"");   // silent game track
    Ffmpeg($"-f lavfi -i sine=frequency=700:sample_rate=48000 -t 4 -ac 2 -c:a pcm_s16le \"{(string)Call(rec, "ChatSidecar", clip)}\"");             // "Discord" track (ffmpeg sine = -18 dBFS)
    var mixT = share.GetNestedType("Mix", ANY);
    double Shared(bool chatOn)
    {
        var mix = Activator.CreateInstance(mixT); mixT.GetField("chatOn").SetValue(mix, chatOn);
        var t = (Task<string>)Call(share, "RenderAsync", "g.mp4", "g_share_" + chatOn, 0, 0, 0.0, 0.0, 4.0, null, mix);
        string p = t.GetAwaiter().GetResult(); double v = p == null ? 999 : MaxVolume(p); try { if (p != null) File.Delete(p); } catch { }
        return v;
    }
    double muted = Shared(false), live = Shared(true);
    Check("G. share leaves a muted chat track out", muted < -60 && live > -30, $"chat muted {muted:F1} dB · chat on {live:F1} dB");
}
else Skip("G. share leaves a muted chat track out", "no ffmpeg");

// H — replay spanning a window switch (segments of different sizes): earlier footage letterboxed + joined, full length
if (haveFf)
{
    string prev = Path.Combine(tmp, "h_prev.mp4"), cur = Path.Combine(tmp, "h_cur.mp4"), dst = Path.Combine(tmp, "h_replay.mp4"), png = Path.Combine(tmp, "h.png");
    // prev at 4 fps = a mostly-static captured window (WGC delivers few frames): the join must NOT drag the game part down to it
    Ffmpeg($"-f lavfi -i color=c=green:s=1920x1080:r=4:d=30 -f lavfi -i sine=f=440:r=48000:d=30 -ac 2 -c:v libx264 -preset ultrafast -c:a aac -shortest \"{prev}\"");
    Ffmpeg($"-f lavfi -i color=c=blue:s=3440x1440:r=30:d=10 -f lavfi -i sine=f=880:r=48000:d=10 -ac 2 -c:v libx264 -preset ultrafast -c:a aac -shortest \"{cur}\"");
    bool joined = ((Task<bool>)Call(ffm, "ConcatLastSecondsAsync", prev, cur, 30, dst, 0.0)).GetAwaiter().GetResult();
    double len = joined ? (double)Call(T("ClipLibrary"), "Mp4DurationSeconds", dst) : 0;
    Ffmpeg($"-ss 5 -i \"{dst}\" -frames:v 1 \"{png}\"");
    bool bars = false; int width = 0;
    if (File.Exists(png)) using (var bmp = new System.Drawing.Bitmap(png)) { width = bmp.Width; var bar = bmp.GetPixel(200, bmp.Height / 2); var pic = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2); bars = bar.G < 20 && pic.G > 100; }
    int frames = 0;
    if (joined)
    {
        var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe"),
            $"-v error -select_streams v -count_packets -show_entries stream=nb_read_packets -of csv=p=0 \"{dst}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        int.TryParse(pr.StandardOutput.ReadToEnd().Trim().TrimEnd(','), out frames); pr.WaitForExit();
    }
    // 30 s at the newest segment's 30 fps ~= 900 frames (the slow 4 fps earlier part is repeated up, never the other way)
    Check("H. window-switch replay joined (full length, letterboxed, keeps the frame rate)", joined && Math.Abs(len - 30) < 0.6 && width == 3440 && bars && frames > 850,
        $"joined={joined} length={len:F1}s width={width} letterboxed={bars} frames={frames} (want ~900)");
}
else Skip("H. window-switch replay joined", "no ffmpeg");

// I — same-size replay join keeps the REAL gap between segments (recorder restart ~0.4 s): without it every second before
// the join played early against the continuous game audio. prev started 20.5 s before cur but holds only 20 s of video.
if (haveFf)
{
    string prev = Path.Combine(tmp, "i_prev.mp4"), cur = Path.Combine(tmp, "i_cur.mp4"), dst = Path.Combine(tmp, "i_replay.mp4");
    Ffmpeg($"-f lavfi -i color=c=green:s=1280x720:r=30:d=20 -f lavfi -i sine=f=440:r=48000:d=20 -ac 2 -c:v libx264 -preset ultrafast -g 15 -c:a aac -shortest \"{prev}\"");
    Ffmpeg($"-f lavfi -i color=c=blue:s=1280x720:r=30:d=10 -f lavfi -i sine=f=880:r=48000:d=10 -ac 2 -c:v libx264 -preset ultrafast -g 15 -c:a aac -shortest \"{cur}\"");
    bool joined = ((Task<bool>)Call(ffm, "ConcatLastSecondsAsync", prev, cur, 25, dst, 20.5)).GetAwaiter().GetResult();
    double gap = 0;
    if (joined)
    {
        var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe"),
            $"-v error -select_streams v -show_entries packet=pts_time -of csv=p=0 \"{dst}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        var pts = pr.StandardOutput.ReadToEnd().Split((char)10).Select(l => double.TryParse(l.Trim().TrimEnd(','), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
            .Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList();
        pr.WaitForExit();
        for (int i = 1; i < pts.Count; i++) gap = Math.Max(gap, pts[i] - pts[i - 1]);
    }
    Check("I. replay join keeps the real segment gap", joined && Math.Abs(gap - (0.5 + 1 / 30.0)) < 0.06, $"joined={joined} largest frame gap={gap * 1000:F0} ms (want ~533)");
}
else Skip("I. replay join keeps the real segment gap", "no ffmpeg");

// J — update prompt reads the release notes: the "What's new" list (new format) or the commit subject (older releases)
{
    var upd = T("AppUpdater");
    string notesNew = (string)Call(upd, "CleanNotes", "## What's new\n- Fix replay sync\n- Numpad hotkeys\n\n---\n**Download `SpartaClipsSetup.exe` below**");
    string notesOld = (string)Call(upd, "CleanNotes", "**Download below.**\n\nChanges in this build: Hotkeys: allow numpad\n\nLong body line one\nline two");
    Check("J. update notes: new format", notesNew == "•  Fix replay sync\n•  Numpad hotkeys", notesNew.Replace("\n", " | "));
    Check("J. update notes: older releases", notesOld == "•  Hotkeys: allow numpad", notesOld.Replace("\n", " | "));
}

// E — share resolution ladder for a 60 s 3440x1440 @ 26 fps clip
int H(int tierMb) { double vbps = (tierMb * 8.0 * 1048576 * 0.95 - 160_000 * 60) / 60; return (int)Call(share, "ShareHeight", vbps, 3440, 1440, 26.0); }
Check("E. share 10 MB -> 480p", H(10) == 480, H(10) + "p");
Check("E. share 25 MB -> 720p", H(25) == 720, H(25) + "p");
Check("E. share 500 MB stays native", H(500) == 0, H(500) == 0 ? "native" : H(500) + "p");

// F — capture-target filter
Check("F. null window rejected", !(bool)Call(rec, "IsCapturableApp", IntPtr.Zero));
Check("F. desktop (shell) window rejected", !(bool)Call(rec, "IsCapturableApp", GetShellWindow()));

try { Directory.Delete(tmp, true); } catch { }
Console.WriteLine(fails == 0 ? "\nALL PASS" : $"\n{fails} FAILED");
return fails == 0 ? 0 : 1;

void Ffmpeg(string args)
{
    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ff, "-y -hide_banner -loglevel error " + args) { UseShellExecute = false, CreateNoWindow = true });
    p.WaitForExit();
}
double MaxVolume(string file)
{
    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ff, $"-hide_banner -i \"{file}\" -map 0:a:0 -af volumedetect -f null -") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
    string err = p.StandardError.ReadToEnd(); p.WaitForExit();
    var m = System.Text.RegularExpressions.Regex.Match(err, @"max_volume: (-?[0-9.]+) dB");
    return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -999;
}
[DllImport("user32.dll")] static extern IntPtr GetShellWindow();

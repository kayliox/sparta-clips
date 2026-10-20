// League of Legends (and TFT, which runs the same client + endpoint) highlight observer. Reads Riot's OFFICIAL
// Live Client Data API over localhost (https://127.0.0.1:2999/liveclientdata/) — documented, in-game, and
// Vanguard-safe (no memory reads / injection; the same surface Medal + Overwolf use). Ported from Lowkey's
// utils/matchObservers/LeagueOfLegends.js: fetch the active player once, then diff eventdata for new kills/assists.
using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpartaClips
{
    internal sealed class LeagueObserver : IMatchObserver
    {
        public string Game => "League of Legends";

        private const string Base = "https://127.0.0.1:2999/liveclientdata/";
        // The Live Client API serves a self-signed cert; trust it ONLY for 127.0.0.1:2999, nothing else.
        private static readonly HttpClient _http = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (msg, _, _, _) =>
                msg.RequestUri != null && msg.RequestUri.Host == "127.0.0.1" && msg.RequestUri.Port == 2999
        })
        { Timeout = TimeSpan.FromSeconds(3) };

        private string _me = "";        // active summoner name (set per match)
        private int _lastCount;          // number of events seen last tick
        private int _errors;             // consecutive failures → go idle past the threshold
        private const int ErrThreshold = 10;

        // The in-match game process (not the LeagueClient launcher). TFT runs this same exe.
        public bool ProcessPresent() { try { return Process.GetProcessesByName("League of Legends").Any(); } catch { return false; } }

        public async Task PollAsync(Action<string> onHighlight, CancellationToken ct)
        {
            try
            {
                if (_me.Length == 0)
                {
                    string? name = await GetAsync("activeplayername", ct);   // returns a quoted string, e.g. "coolSummoner"
                    if (string.IsNullOrWhiteSpace(name)) { Bump(); return; }
                    _me = name.Trim().Trim('"');
                    _lastCount = 0;   // attach fresh — don't replay events from before we connected
                    _errors = 0;
                    DiagLog.Line("HILITE", "LoL match: " + _me);
                    return;           // give eventdata its own tick
                }

                string? json = await GetAsync("eventdata", ct);
                if (string.IsNullOrWhiteSpace(json)) { Bump(); return; }
                _errors = 0;

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Events", out var events) || events.ValueKind != JsonValueKind.Array) return;
                int count = events.GetArrayLength();
                if (count <= _lastCount) { _lastCount = count; return; }   // count resets on a new game → resync, don't fire

                // Take the BEST label among this tick's new events (a multikill outranks the kills that made it), so the
                // single clip the cooldown allows is named for the highest moment in the burst.
                string? best = null; int bestRank = -1;
                for (int i = _lastCount; i < count; i++)
                {
                    string? label = Classify(events[i]);
                    if (label == null) continue;
                    int rank = Rank(label);
                    if (rank > bestRank) { bestRank = rank; best = label; }
                }
                _lastCount = count;
                if (best != null) onHighlight(best);
            }
            catch (OperationCanceledException) { throw; }
            catch { Bump(); }
        }

        // After repeated failures (match ended / not in game / API down) reset so a new match re-initialises cleanly.
        private void Bump() { if (++_errors >= ErrThreshold) { _me = ""; _lastCount = 0; _errors = 0; } }

        private static async Task<string?> GetAsync(string endpoint, CancellationToken ct)
        {
            try { using var r = await _http.GetAsync(Base + endpoint, ct); return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync(ct) : null; }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }
        private static string Str(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        // Map a Live-Client event to a highlight label when the ACTIVE PLAYER is the actor, else null. The 2999
        // eventdata feed exposes kills/assists + objectives (dragon/baron/herald/turret), multikills, first blood, ace.
        private string? Classify(JsonElement e)
        {
            bool Me(string f) => Str(e, f) == _me;
            bool Assisted() => e.TryGetProperty("Assisters", out var a) && a.ValueKind == JsonValueKind.Array
                               && a.EnumerateArray().Any(x => x.GetString() == _me);
            return Str(e, "EventName") switch
            {
                "ChampionKill" => Me("KillerName") ? "Kill" : Assisted() ? "Assist" : null,
                "Multikill"    => Me("KillerName") ? MultikillLabel(e) : null,
                "FirstBlood"   => (Me("Recipient") || Me("KillerName")) ? "First Blood" : null,
                "Ace"          => Me("Acer") ? "Ace" : null,
                "DragonKill"   => (Me("KillerName") || Assisted()) ? "Dragon" : null,
                "BaronKill"    => (Me("KillerName") || Assisted()) ? "Baron" : null,
                "HeraldKill"   => (Me("KillerName") || Assisted()) ? "Herald" : null,
                "TurretKilled" => Me("KillerName") ? "Turret" : null,
                _ => null,
            };
        }

        private static string MultikillLabel(JsonElement e)
        {
            int n = e.TryGetProperty("KillStreak", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 2;
            return n >= 5 ? "Penta Kill" : n == 4 ? "Quadra Kill" : n == 3 ? "Triple Kill" : "Double Kill";
        }

        // Priority when several highlights land in one poll — the biggest moment wins the clip's label.
        private static int Rank(string label) => label switch
        {
            "Penta Kill" => 11, "Quadra Kill" => 10, "Triple Kill" => 9, "Ace" => 8, "Baron" => 7,
            "Double Kill" => 6, "Dragon" => 5, "Herald" => 4, "Turret" => 3, "First Blood" => 2, "Kill" => 1, _ => 0,
        };
    }
}

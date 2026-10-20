// Contract for a per-game match observer: cheaply tells the coordinator whether its game is running, and
// when polled, raises a label for each NEW highlight-worthy event (a kill/assist). Anti-cheat-safe by design —
// observers only read official local game APIs over localhost; never the game's memory.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SpartaClips
{
    internal interface IMatchObserver
    {
        string Game { get; }            // human label, e.g. "League of Legends"
        bool ProcessPresent();          // cheap: is the in-match game process running right now?
        Task PollAsync(Action<string> onHighlight, CancellationToken ct);   // onHighlight(label) per new event; coordinator owns cooldown + the actual save
    }
}

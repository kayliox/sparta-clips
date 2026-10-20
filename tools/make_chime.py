# make_chime.py — synthesizes the "clip captured" sound ("Neon Charge": a detuned synthwave chord charging up through
# a resonant filter into a metallic "shing" + sub hit, with a short reverb tail) → Services/Capture/chime.wav, which is
# embedded in the app and played by ClipChime. 48 kHz stereo 16-bit, peak -7 dBFS. Deterministic (seeded noise).
# Edit, then run:  python tools/make_chime.py   (needs numpy)
import numpy as np, wave, os
SR = 48000
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'Services', 'Capture')
rng = np.random.default_rng(42)

def t_(dur): return np.arange(int(dur * SR)) / SR
def saw(freq_t):                       # freq can be an array (glides); naive saw, filtered later
    ph = np.cumsum(freq_t) / SR; return 2 * (ph - np.floor(ph + 0.5))
def sq(freq_t, duty=0.5):
    ph = np.cumsum(freq_t) / SR; return np.where((ph % 1) < duty, 1.0, -1.0)
def sine(freq_t): return np.sin(2 * np.pi * np.cumsum(freq_t) / SR)
def adsr(n, a, tau):
    t = np.arange(n) / SR; return np.clip(t / max(a, 1e-4), 0, 1) * np.exp(-np.maximum(t - a, 0) / tau)

def svf_lp(x, cutoff, q=0.7):          # TPT state-variable low-pass (Zavalishin) — stable at any cutoff/resonance
    cutoff = np.broadcast_to(np.asarray(cutoff, float), x.shape)
    y = np.zeros_like(x); ic1 = ic2 = 0.0; k = 1.0 / q
    for i in range(len(x)):
        g = np.tan(np.pi * min(cutoff[i], SR * 0.45) / SR)
        a1 = 1.0 / (1.0 + g * (g + k)); a2 = g * a1; a3 = g * a2
        v3 = x[i] - ic2; v1 = a1 * ic1 + a2 * v3; v2 = ic2 + a2 * ic1 + a3 * v3
        ic1 = 2 * v1 - ic1; ic2 = 2 * v2 - ic2; y[i] = v2
    return y

def reverb(x, mix=0.25, room=0.82):     # tiny Schroeder: 4 combs + 2 allpasses → "big space" tail
    out = np.zeros(len(x) + int(0.35 * SR)); xi = np.concatenate([x, np.zeros(len(out) - len(x))])
    wet = np.zeros_like(out)
    for d in (1557, 1617, 1491, 1422):
        buf = np.zeros_like(out)
        for i in range(len(out)): buf[i] = xi[i] + (room * buf[i - d] if i >= d else 0)
        wet += buf
    for d, g in ((225, 0.5), (556, 0.5)):
        y = np.zeros_like(wet)
        for i in range(len(wet)): y[i] = -g * wet[i] + (wet[i - d] + g * y[i - d] if i >= d else 0)
        wet = y
    return xi * (1 - mix) + wet / 4 * mix

def stereo(m, width_ms=11, detune=0.0):  # Haas widening: R slightly delayed
    d = int(width_ms / 1000 * SR); r = np.concatenate([np.zeros(d), m])[:len(m)]
    return np.stack([m, 0.82 * m + 0.18 * r if False else r * 0.9 + m * 0.1], axis=1)

def mix_at(total, parts):
    out = np.zeros(int(total * SR))
    for start, sig, g in parts:
        i = int(start * SR); n = min(len(sig), len(out) - i); out[i:i + n] += g * sig[:n]
    return out

def save(name, st):
    st = st / np.max(np.abs(st)) * 10 ** (-7 / 20)
    f = int(0.02 * SR); st[-f:] *= np.linspace(1, 0, f)[:, None]
    with wave.open(os.path.join(OUT, name), 'wb') as w:
        w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR); w.writeframes((st * 32767).astype('<i2').tobytes())
    print(name, f"{len(st) / SR * 1000:.0f} ms")

# ── 2 · NEON CHARGE: detuned supersaw chord charging up (filter opens) → metallic "shing" + big tail ──
n = int(0.55 * SR); tt = np.arange(n) / SR
chord = np.zeros(n)
for f in (146.83, 220.0, 293.66, 349.23, 440.0):        # D minor-add, synthwave voicing
    for d in (-0.012, -0.004, 0.004, 0.012): chord += saw(np.full(n, f * (1 + d)))
chord = svf_lp(chord / 20, 200 + 6800 * (tt / 0.55) ** 2.2, q=1.6) * np.clip(tt / 0.08, 0, 1)
m = t_(0.7); shing = (np.sin(2*np.pi*2349*m) + 0.6*np.sin(2*np.pi*3523*m) + 0.4*np.sin(2*np.pi*5873*m)*np.exp(-m/0.04)) * adsr(len(m), 0.001, 0.18)
shing += svf_lp(rng.standard_normal(len(m)), 9000) * adsr(len(m), 0.001, 0.025) * 0.6
charge = mix_at(1.0, [(0.0, chord, 0.8), (0.50, shing, 0.55), (0.50, sine(np.full(int(0.4*SR), 73.4)) * adsr(int(0.4*SR), 0.003, 0.14), 0.9)])
save("chime.wav", stereo(reverb(charge, 0.32, 0.85), 14))


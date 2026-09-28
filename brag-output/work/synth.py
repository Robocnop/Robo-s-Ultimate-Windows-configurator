"""Bande-son de la vidéo : musique + effets synthétisés, calés sur timeline.json.

Tonalité la mineur, 120 BPM. La grille de mesures (2 s) est ancrée sur l'outro pour
que l'accord final tombe pile sur le titre.
"""
import json
import wave

import numpy as np

SR = 44100
TL = json.load(open('timeline.json', encoding='utf-8'))
DUR = TL['duration']
N = int(DUR * SR)
rng = np.random.default_rng(7)

BEAT = 0.5
BAR = 4 * BEAT
OUTRO = TL['outro']
BAR0 = OUTRO - 7 * BAR              # 4.35 s
DRUMS_IN = BAR0 + 2 * BEAT          # 5.35 s : le beat part juste après le double-clic


def hz(midi):
    return 440.0 * 2 ** ((midi - 69) / 12)


def buf():
    return np.zeros((N, 2))


def place(dst, sig, t, gain=1.0, pan=0.0):
    """Ajoute un signal mono à l'instant t, panoramique à puissance constante."""
    i = int(t * SR)
    if i >= N:
        return
    l, r = np.cos((pan + 1) * np.pi / 4), np.sin((pan + 1) * np.pi / 4)
    st = np.stack([sig * l * 1.414, sig * r * 1.414], axis=1)
    n = min(len(st), N - i)
    dst[i:i + n] += st[:n] * gain


def fft_filter(x, lo=None, hi=None, order=2):
    """Passe-haut / passe-bas (réponse de type Butterworth) dans le domaine fréquentiel."""
    n = len(x)
    f = np.fft.rfftfreq(n, 1 / SR)
    g = np.ones_like(f)
    if hi:
        g /= np.sqrt(1 + (f / hi) ** (2 * order))
    if lo:
        g /= np.sqrt(1 + (lo / np.maximum(f, 1e-6)) ** (2 * order))
    if x.ndim == 2:
        return np.stack([np.fft.irfft(np.fft.rfft(x[:, c]) * g, n) for c in range(x.shape[1])], axis=1)
    return np.fft.irfft(np.fft.rfft(x) * g, n)


def env(n, a, r):
    e = np.ones(n)
    na, nr = max(1, int(a * SR)), max(1, int(r * SR))
    e[:na] = np.linspace(0, 1, na)
    e[-nr:] *= np.linspace(1, 0, nr)
    return e


def additive(freq, dur, harmonics=10, tilt=1.6, detune_cents=0.0):
    t = np.arange(int(dur * SR)) / SR
    f = freq * 2 ** (detune_cents / 1200)
    out = np.zeros_like(t)
    for k in range(1, harmonics + 1):
        if f * k > 12000:
            break
        out += np.sin(2 * np.pi * f * k * t + k * 0.7) / k ** tilt
    return out


def pluck(freq, dur=0.45, bright=0.35):
    t = np.arange(int(dur * SR)) / SR
    s = np.sin(2 * np.pi * freq * t) + bright * np.sin(2 * np.pi * 2 * freq * t) * np.exp(-t * 18)
    return s * np.exp(-t * 7) * env(len(t), 0.003, 0.02)


def bell(freq, dur=1.6):
    t = np.arange(int(dur * SR)) / SR
    s = (np.sin(2 * np.pi * freq * t) * np.exp(-t * 2.2)
         + 0.35 * np.sin(2 * np.pi * freq * 2.76 * t) * np.exp(-t * 4.5)
         + 0.15 * np.sin(2 * np.pi * freq * 5.4 * t) * np.exp(-t * 8))
    return s * env(len(t), 0.004, 0.05)


def kick():
    t = np.arange(int(0.35 * SR)) / SR
    f = 45 + 75 * np.exp(-t * 28)
    return np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t * 9) + 0.08 * rng.standard_normal(len(t)) * np.exp(-t * 180)


def noise_burst(dur, decay, lo=None, hi=None):
    t = np.arange(int(dur * SR)) / SR
    return fft_filter(rng.standard_normal(len(t)), lo=lo, hi=hi) * np.exp(-t * decay)


def reverb(x, seconds=1.9, damp=4500):
    n = int(seconds * SR)
    t = np.arange(n) / SR
    ir = fft_filter(rng.standard_normal((n, 2)) * np.exp(-t * 3.2)[:, None], hi=damp)
    ir[: int(0.012 * SR)] = 0                                 # pré-délai
    ir /= np.sqrt((ir ** 2).sum(axis=0))
    m = N + n
    return np.stack([np.fft.irfft(np.fft.rfft(x[:, c], m) * np.fft.rfft(ir[:, c], m), m)[:N] for c in range(2)], axis=1)


# ---------------------------------------------------------------------------
# Harmonie : Am (intro) | Am F C G F G | Am (outro)
# ---------------------------------------------------------------------------
CHORDS = {'Am': [57, 60, 64, 69], 'F': [53, 57, 60, 65], 'C': [55, 60, 64, 67], 'G': [55, 59, 62, 67]}
ROOT = {'Am': 45, 'F': 41, 'C': 48, 'G': 43}
ARP = {'Am': [69, 72, 76, 81], 'F': [65, 69, 72, 77], 'C': [67, 72, 76, 79], 'G': [67, 71, 74, 79]}
bars = [(0.0, 'Am')] + [(BAR0 + BAR + i * BAR, c) for i, c in enumerate(['Am', 'F', 'C', 'G', 'F', 'G'])] + [(OUTRO, 'Am')]
segments = [(t0, bars[i + 1][0] if i + 1 < len(bars) else DUR, name) for i, (t0, name) in enumerate(bars)]

pad, bass, drums, lead, sfx = buf(), buf(), buf(), buf(), buf()

for t0, t1, name in segments:
    for m in CHORDS[name]:
        for det, pan in ((-6, -0.5), (6, 0.5)):
            s = additive(hz(m), t1 - t0 + 0.5, harmonics=9, tilt=1.7, detune_cents=det)
            place(pad, s * env(len(s), 1.6 if t0 == 0 else 0.35, 0.5), t0, 0.045, pan)

# Basse en croches pendant le beat, puis note tenue sur l'outro
for t0, t1, name in segments:
    t = max(t0, DRUMS_IN)
    while t < min(t1, OUTRO) - 1e-6:
        s = additive(hz(ROOT[name]), 0.24, harmonics=14, tilt=1.0)
        place(bass, s * env(len(s), 0.004, 0.08) * np.exp(-np.arange(len(s)) / SR * 5), t, 0.16)
        t += BEAT / 2
s = additive(hz(45), 3.5, harmonics=10, tilt=1.2)
place(bass, s * env(len(s), 0.005, 1.8) * np.exp(-np.arange(len(s)) / SR * 0.9), OUTRO, 0.18)

# Batterie : kick sur chaque temps, charley en contretemps, clap sur 2 et 4
k = kick()
t, i = DRUMS_IN, 0
while t < OUTRO - 1e-6:
    place(drums, k, t, 0.55)
    place(drums, noise_burst(0.06, 70, lo=7000), t + BEAT / 2, 0.05, 0.25)
    if t >= BAR0 + BAR - 1e-6 and i % 2 == 1:
        place(drums, noise_burst(0.18, 22, lo=900, hi=5000) + 0.3 * pluck(200, 0.18, 0), t, 0.10, -0.1)
    t += BEAT
    i += 1
place(drums, k, OUTRO, 0.6)
place(drums, noise_burst(2.2, 1.6, lo=3000, hi=12000), OUTRO, 0.05)   # cymbale douce

# Arpège discret pendant le terminal et les cartes
for t0, t1, name in segments:
    if t0 < BAR0 + BAR or t0 >= OUTRO:
        continue
    for s16 in range(8):
        place(lead, pluck(hz(ARP[name][s16 % 4]), 0.4, 0.25), t0 + s16 * BEAT / 2, 0.035, 0.35 if s16 % 2 else -0.35)

# Carillon final
for j, m in enumerate([69, 72, 76, 81]):
    place(lead, bell(hz(m + 12), 2.6), OUTRO + 0.12 * j, 0.06, -0.3 + 0.2 * j)

# ---------------------------------------------------------------------------
# Effets, dans la même tonalité (la mineur pentatonique)
# ---------------------------------------------------------------------------
penta = [81, 84, 86, 88, 91]
for i, tc in enumerate(TL['chores']):
    place(sfx, pluck(hz(penta[i]), 0.35, 0.2), tc, 0.07, -0.4 + 0.2 * i)
place(sfx, bell(hz(69), 1.4), TL['hook2'], 0.05)

rn = int(1.0 * SR)
place(sfx, fft_filter(rng.standard_normal(rn), lo=800, hi=6000) * np.linspace(0, 1, rn) ** 2.2, 3.95, 0.05)   # montée
for tc in TL['clicks']:
    place(sfx, noise_burst(0.03, 260, lo=1500, hi=6000) + 0.4 * pluck(1320, 0.03, 0), tc, 0.22)

for ln in TL['termLines']:                                   # ticks du terminal, en fond
    place(sfx, pluck(hz(88 if ln['head'] else 93), 0.12, 0.1), ln['t'], 0.04 if ln['head'] else 0.025, 0.15)

sn = int(0.9 * SR)                                           # passage en thème sombre
place(sfx, fft_filter(rng.standard_normal(sn), lo=400, hi=4000) * np.linspace(0, 1, sn) ** 3, TL['dark'] - 0.85, 0.06)
for j, m in enumerate([76, 81, 84]):
    place(sfx, bell(hz(m), 1.8), TL['dark'] + 0.04 * j, 0.05, -0.2 + 0.2 * j)
for tc in TL['chips']:
    place(sfx, pluck(hz(88), 0.25, 0.2), tc, 0.035, 0.3)

for i, tc in enumerate(TL['cards']):                         # cartes : souffle + pastille
    wn = int(0.4 * SR)
    place(sfx, fft_filter(rng.standard_normal(wn), lo=600, hi=3500) * np.sin(np.linspace(0, np.pi, wn)) ** 2, tc - 0.05, 0.05, 0.5)
    place(sfx, pluck(hz([81, 84, 88][i]), 0.35, 0.25), tc + 0.33, 0.06)
place(sfx, bell(hz(64), 1.2), TL['resume'], 0.035)

# ---------------------------------------------------------------------------
# Mixage
# ---------------------------------------------------------------------------
duck = np.ones(N)                                            # pompage léger par le kick
t = DRUMS_IN
while t < OUTRO:
    i = int(t * SR)
    n = min(int(0.3 * SR), N - i)
    duck[i:i + n] = np.minimum(duck[i:i + n], 1 - 0.35 * np.exp(-np.arange(n) / SR * 12))
    t += BEAT
pad *= duck[:, None]
bass *= duck[:, None]

pad = fft_filter(pad, hi=3200)
drums = fft_filter(drums, lo=30)
sfx = fft_filter(sfx, hi=9000)
mixed = pad + bass + drums + lead + sfx + 0.32 * reverb(pad * 0.6 + lead + sfx * 0.9 + drums * 0.08)

fade = np.ones(N)
fade[: int(0.02 * SR)] = np.linspace(0, 1, int(0.02 * SR))
fo = int(1.4 * SR)
fade[-fo:] = np.linspace(1, 0, fo) ** 1.5
mixed *= fade[:, None]
mixed /= np.abs(mixed).max()
mixed = np.tanh(mixed * 1.4) / np.tanh(1.4)                  # limiteur doux
mixed *= 10 ** (-1.0 / 20) / np.abs(mixed).max()

with wave.open('audio.wav', 'wb') as w:
    w.setnchannels(2)
    w.setsampwidth(2)
    w.setframerate(SR)
    w.writeframes((mixed * 32767).astype('<i2').tobytes())
print(f'audio.wav : {DUR:.1f} s, grille à {BAR0:.2f} s, beat à {DRUMS_IN:.2f} s')

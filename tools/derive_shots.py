"""Make the AK and pistol shots (and every far version) from one real recording (D53).

    python tools/derive_shots.py            # reads Assets/Sounds/shot_mosin.wav, writes the rest next to it

A stopgap until real recordings of each gun exist (Tom has only the Mosin). From the Mosin shot:
  * shot_ak: pitched up 12 % (resampled: shorter and higher), the low end thinned, cut at 0.6 s with a fade, so a
    burst at 600 rounds a minute doesn't smear into one roar;
  * shot_pistol: pitched up 35 %, much less low end, cut at 0.45 s, a little quieter;
  * shot_far_<gun>: each shot as heard far away: mono, low-passed at ~500 Hz (air and ground eat the highs), with a
    longer fade.
Run it again whenever shot_mosin.wav changes. Delete a derived file and drop in a real recording to replace it.
"""
import os, wave
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOUNDS = os.path.join(HERE, "Assets", "Sounds")


def load(path):
    w = wave.open(path)
    sr, ch = w.getframerate(), w.getnchannels()
    x = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32).reshape(-1, ch) / 32768
    return x, sr


def save(path, a, sr):
    a = np.clip(a, -1, 1)
    if a.ndim == 1:
        a = a[:, None]
    o = wave.open(path, "wb")
    o.setnchannels(a.shape[1]); o.setsampwidth(2); o.setframerate(sr)
    o.writeframes((a * 32767).astype(np.int16).tobytes())
    o.close()


def pitch_up(x, factor):
    """Resample shorter by `factor` (higher and quicker, like a smaller, faster report)."""
    n = int(len(x) / factor)
    src = np.linspace(0, len(x) - 1, n)
    return np.stack([np.interp(src, np.arange(len(x)), x[:, c]) for c in range(x.shape[1])], axis=1)


def one_pole(x, hz, sr, high=False):
    """A one-pole low-pass (or the high-pass left over), along the time axis, per channel."""
    a = 1 - np.exp(-2 * np.pi * hz / sr)
    y = np.zeros_like(x)
    z = np.zeros(x.shape[1])
    for i in range(len(x)):
        z += a * (x[i] - z)
        y[i] = z
    return x - y if high else y


def cut(x, seconds, sr, fade=0.12):
    n = min(len(x), int(seconds * sr))
    y = x[:n].copy()
    f = min(n, int(fade * sr))
    y[n - f:] *= np.linspace(1, 0, f)[:, None]
    return y


def normal(x, peak):
    return x / (abs(x).max() + 1e-9) * peak


def far(x, sr):
    m = x.mean(axis=1, keepdims=True)
    m = one_pole(one_pole(m, 500, sr), 500, sr)
    m = np.concatenate([m, np.zeros((int(0.6 * sr), 1))])
    return normal(m, 0.7)


def main():
    mosin, sr = load(os.path.join(SOUNDS, "shot_mosin.wav"))
    # AK: up 12 %, thinner low end (keep 70 % of what's under ~180 Hz), short
    ak = pitch_up(mosin, 1.12)
    ak = ak - 0.3 * one_pole(ak, 180, sr)
    ak = normal(cut(ak, 0.6, sr), 0.72)
    # pistol: up 35 %, much less low end, shorter, quieter
    pistol = pitch_up(mosin, 1.35)
    pistol = pistol - 0.6 * one_pole(pistol, 220, sr)
    pistol = normal(cut(pistol, 0.45, sr, fade=0.1), 0.6)
    save(os.path.join(SOUNDS, "shot_ak.wav"), ak, sr)
    save(os.path.join(SOUNDS, "shot_pistol.wav"), pistol, sr)
    for gun, shot in (("ak", ak), ("pistol", pistol)):
        save(os.path.join(SOUNDS, f"shot_far_{gun}.wav"), far(shot, sr), sr)
    print(f"ak {len(ak)/sr:.2f} s, pistol {len(pistol)/sr:.2f} s, and their far versions")


if __name__ == "__main__":
    main()

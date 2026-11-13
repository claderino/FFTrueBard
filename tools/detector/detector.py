#!/usr/bin/env python3
"""FFTrueBard note detector.

Captures the guitar signal (NUX Mighty Space with USB routing set to "Dry Out"),
runs YIN pitch detection, and streams note events to the FFTrueBard Dalamud
plugin as JSON lines over localhost TCP.

Protocol (one JSON object per line, LF-terminated):
    {"note": "E2", "freq": 82.41, "amp": 0.42, "t": 1699999999999}

Usage:
    python detector.py --list-devices
    python detector.py                       # default input device, port 4242
    python detector.py --device NUX --port 4242 --retrigger-ms 200
"""

from __future__ import annotations

import argparse
import json
import math
import queue
import socket
import sys
import threading
import time
from collections import deque

import numpy as np
import sounddevice as sd

NOTE_NAMES = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]


def freq_to_note(freq: float) -> tuple[str, int]:
    """Return (note name like 'A2', cents deviation)."""
    midi = 69.0 + 12.0 * math.log2(freq / 440.0)
    m = int(round(midi))
    cents = int(round((midi - m) * 100))
    return f"{NOTE_NAMES[m % 12]}{m // 12 - 1}", cents


def yin_pitch(samples: np.ndarray, sr: int, fmin: float, fmax: float, threshold: float) -> float:
    """YIN fundamental frequency estimator. Returns 0.0 when unvoiced."""
    y = samples.astype(np.float64)
    if math.sqrt(float(np.mean(y * y))) < 1e-4:
        return 0.0

    w = min(len(y) // 2, 1024)
    max_tau = int(sr / fmin)
    min_tau = max(2, int(sr / fmax))
    if len(y) < max_tau + w or w <= min_tau + 2:
        return 0.0

    y0 = y[:w]
    d = np.zeros(max_tau + 1)
    for tau in range(1, max_tau + 1):
        delta = y0 - y[tau:tau + w]
        d[tau] = np.dot(delta, delta)

    # Cumulative mean normalized difference function.
    d[0] = 0.0
    dsum = np.cumsum(d)
    cmnd = np.ones_like(d)
    nz = dsum > 0
    cmnd[nz] = d[nz] * np.arange(1, len(d))[nz] / dsum[nz]

    below = np.where(cmnd[min_tau:max_tau] < threshold)[0]
    if len(below) == 0:
        return 0.0
    tau = int(below[0]) + min_tau

    # Parabolic interpolation around the minimum.
    if 1 < tau < max_tau - 1:
        s0, s1, s2 = cmnd[tau - 1], cmnd[tau], cmnd[tau + 1]
        denom = 2.0 * (2.0 * s1 - s2 - s0)
        if denom != 0.0:
            tau += (s2 - s0) / denom

    return sr / tau


class LinkSender:
    """Persists a TCP connection to the plugin, reconnecting as needed."""

    def __init__(self, port: int):
        self.port = port
        self.sock: socket.socket | None = None

    def _connect(self) -> None:
        self.sock = socket.create_connection(("127.0.0.1", self.port), timeout=2)
        self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        print(f"[fftruebard] connected to plugin on 127.0.0.1:{self.port}")

    def send(self, payload: dict) -> None:
        line = (json.dumps(payload) + "\n").encode()
        while True:
            try:
                if self.sock is None:
                    self._connect()
                assert self.sock is not None
                self.sock.sendall(line)
                return
            except OSError:
                try:
                    if self.sock is not None:
                        self.sock.close()
                except OSError:
                    pass
                self.sock = None
                time.sleep(1.0)
                # Retry forever; the plugin window may simply not be open yet.


def main() -> int:
    ap = argparse.ArgumentParser(description="FFTrueBard guitar note detector")
    ap.add_argument("--device", default=None, help="input device name substring or index")
    ap.add_argument("--port", type=int, default=4242, help="plugin TCP port (default 4242)")
    ap.add_argument("--samplerate", type=int, default=48000)
    ap.add_argument("--blocksize", type=int, default=512)
    ap.add_argument("--threshold", type=float, default=0.15, help="YIN aperiodicity threshold")
    ap.add_argument("--rms", type=float, default=0.004, help="amplitude gate (0-1 float scale)")
    ap.add_argument("--retrigger-ms", type=int, default=150, help="min ms between repeats of the same note")
    ap.add_argument("--stable-frames", type=int, default=2, help="consecutive agreeing frames to confirm a note")
    ap.add_argument("--list-devices", action="store_true")
    args = ap.parse_args()

    if args.list_devices:
        print(sd.query_devices())
        return 0

    device_idx: int | None = None
    if args.device is not None:
        if args.device.isdigit():
            device_idx = int(args.device)
        else:
            for i, dev in enumerate(sd.query_devices()):
                if args.device.lower() in dev["name"].lower() and dev["max_input_channels"] > 0:
                    device_idx = i
                    break
            if device_idx is None:
                print(f"[fftruebard] no input device matching {args.device!r}; use --list-devices")
                return 1

    extra = None
    try:
        extra = sd.WasapiSettings(exclusive=False)  # prefer WASAPI over high-latency MME
    except Exception:
        pass

    q: queue.Queue[np.ndarray] = queue.Queue()
    frame_len = 2048  # ~43 ms @ 48 kHz

    def audio_callback(indata, frames, time_info, status):
        if status:
            print(f"[audio] {status}", file=sys.stderr)
        q.put(indata[:, 0].copy())

    sender = LinkSender(args.port)
    last_note: str | None = None
    last_sent_t = 0.0
    candidate_frames: deque[str] = deque(maxlen=max(1, args.stable_frames))

    print(f"[fftruebard] detector running - device={args.device or 'default input'}, "
          f"{args.samplerate} Hz, streaming notes to 127.0.0.1:{args.port}")

    def detection_loop() -> None:
        nonlocal last_note, last_sent_t
        buf = np.zeros(0, dtype=np.float32)
        while True:
            buf = np.concatenate((buf, q.get()))
            if len(buf) < frame_len:
                continue
            frame, buf = buf[:frame_len], buf[frame_len:]

            rms = math.sqrt(float(np.mean(frame.astype(np.float64) ** 2)))
            if rms < args.rms:
                candidate_frames.clear()
                continue

            freq = yin_pitch(frame, args.samplerate, fmin=70.0, fmax=1300.0, threshold=args.threshold)
            if freq <= 0.0:
                continue

            note, cents = freq_to_note(freq)
            candidate_frames.append(note)
            if candidate_frames.count(note) < args.stable_frames:
                continue

            now = time.monotonic() * 1000.0
            if note == last_note and (now - last_sent_t) < args.retrigger_ms:
                continue

            last_note, last_sent_t = note, now
            print(f"[fftruebard] {note}  {freq:7.2f} Hz  ({cents:+d} cents)  rms {rms:.3f}")
            sender.send({"note": note, "freq": round(freq, 2), "amp": round(rms, 3),
                         "t": int(time.time() * 1000)})

    threading.Thread(target=detection_loop, daemon=True).start()

    with sd.InputStream(device=device_idx, channels=1, samplerate=args.samplerate,
                        blocksize=args.blocksize, dtype="float32",
                        callback=audio_callback, extra_settings=extra):
        try:
            while True:
                time.sleep(0.5)
        except KeyboardInterrupt:
            print("\n[fftruebard] bye")
    return 0


if __name__ == "__main__":
    sys.exit(main())

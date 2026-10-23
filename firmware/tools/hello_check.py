#!/usr/bin/env python3
"""Resets the ESP32 over USB serial, reads its boot output and checks the Hello frame.

    ~/.platformio/penv/bin/python firmware/tools/hello_check.py --port /dev/ttyUSB1 --sliders 5 --kinds 1,1,2
    ~/.platformio/penv/bin/python firmware/tools/hello_check.py --port /dev/ttyUSB1 --guard-only

Safety: on Linux it refuses any port whose USB ID isn't the DIY board's CH340 (1a86:7523), so it never
resets another USB serial device by mistake. Exits 0 only when the expected Hello is seen.
"""
import argparse
import os
import sys
import time

ALLOWED = {"1a86:7523"}


def check_usb_id(port):
    if not port.startswith("/dev/"):
        return
    usb = os.path.realpath(f"/sys/class/tty/{os.path.basename(port)}/device")
    while usb != "/" and not os.path.exists(os.path.join(usb, "idVendor")):
        usb = os.path.dirname(usb)
    try:
        vid = open(os.path.join(usb, "idVendor")).read().strip()
        pid = open(os.path.join(usb, "idProduct")).read().strip()
    except OSError as e:
        sys.exit(f"cannot read the USB ID of {port}: {e}")
    if f"{vid}:{pid}" not in ALLOWED:
        sys.exit(f"{port} is {vid}:{pid}, not the ESP32 {sorted(ALLOWED)}; refusing")
    print(f"{port}: USB {vid}:{pid} OK")


def frames(data):
    """Yields the payload of every valid 0xAA frame (len, payload, XOR checksum) in data."""
    i = 0
    while i < len(data):
        if data[i] != 0xAA or i + 2 >= len(data):
            i += 1
            continue
        n = data[i + 1]
        end = i + 2 + n
        if n == 0 or end >= len(data):
            i += 1
            continue
        payload = data[i + 2:end]
        cs = 0
        for b in payload:
            cs ^= b
        if cs == data[end]:
            yield payload
            i = end + 1
        else:
            i += 1


def parse_hello(p):
    """A version-1 Hello payload (spec §5.2) as a dict, or None when it doesn't add up."""
    if len(p) < 4 or p[0] != 0x01 or p[1] != 0x01:
        return None
    fw_len = p[3]
    i = 4 + fw_len
    if fw_len > 32 or len(p) < i + 7:
        return None
    sliders, buttons = p[i + 5], p[i + 6]
    if len(p) != i + 7 + buttons:
        return None
    return {
        "chip": p[2],
        "firmware": p[4:i].decode("ascii", "replace"),
        "state": p[i],
        "crc": int.from_bytes(p[i + 1:i + 5], "big"),
        "sliders": sliders,
        "kinds": list(p[i + 7:]),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", required=True)
    ap.add_argument("--guard-only", action="store_true")
    ap.add_argument("--sliders", type=int)
    ap.add_argument("--kinds", default="")
    ap.add_argument("--seconds", type=float, default=4.0)
    ap.add_argument("--no-reset", action="store_true")
    a = ap.parse_args()
    check_usb_id(a.port)
    if a.guard_only:
        return
    import serial

    with serial.Serial(a.port, 115200, timeout=0.2) as s:
        if not a.no_reset:
            s.dtr = False
            s.rts = True
            time.sleep(0.1)
            s.rts = False
        end, buf = time.time() + a.seconds, b""
        while time.time() < end:
            buf += s.read(4096)
    text = bytes(b if 32 <= b < 127 or b in (10, 13) else 46 for b in buf).decode("ascii")
    print("--- log lines ---")
    for line in text.splitlines():
        if "[DBG]" in line or "[E]" in line or "rst:" in line:
            print(line.strip()[:160])
    all_frames = list(frames(buf))
    hellos = [p for p in all_frames if p[0] == 0x01]
    print(f"--- {len(all_frames)} frame(s), {len(hellos)} Hello ---")
    for p in hellos:
        print(" ".join(f"{b:02X}" for b in p), "->", parse_hello(p))
    if a.sliders is None:
        return
    kinds = [int(k) for k in a.kinds.split(",") if k != ""]
    print(f"expected: {a.sliders} sliders, kinds {kinds}")
    parsed = [h for h in map(parse_hello, hellos) if h]
    if any(h["sliders"] == a.sliders and h["kinds"] == kinds for h in parsed):
        print("HELLO OK")
    else:
        sys.exit("HELLO MISMATCH or missing")


if __name__ == "__main__":
    main()

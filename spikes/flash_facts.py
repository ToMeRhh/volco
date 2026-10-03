"""SPIKE ONLY (stage 1). Facts about writing firmware parts and NVS, using esptool.

Run with PlatformIO's own Python (it has esptool's dependencies and pyserial):
    Linux:   ~/.platformio/penv/bin/python spikes/flash_facts.py --port /dev/ttyUSB1 --parts-dir parts
    Windows: %USERPROFILE%\\.platformio\\penv\\Scripts\\python.exe spikes\\flash_facts.py --port COM3 --parts-dir parts
Paste the whole output back.

Every esptool call uses --after no_reset, so the app never runs between steps (each call's default
reset re-enters download mode). The final step lets the app boot over a garbage NVS.

Safety: on Linux the script refuses to run unless the port's USB ID is the ESP32's CH340
(1a86:7523); the host also has a Zigbee dongle (10c4:ea60) that must never be written.
"""
import argparse
import hashlib
import os
import subprocess
import sys
import tempfile
import time

NVS_OFFSET, NVS_SIZE = 0x9000, 0x5000
ESPTOOL = os.path.join(os.path.expanduser("~"), ".platformio", "packages", "tool-esptoolpy", "esptool.py")
ALLOWED_USB_IDS = {"1a86:7523"}


def check_usb_id(port):
    """Linux only: refuse any port that isn't the ESP32's USB-serial bridge."""
    if not port.startswith("/dev/"):
        return
    name = os.path.basename(port)
    usb = os.path.realpath(f"/sys/class/tty/{name}/device")
    while usb != "/" and not os.path.exists(os.path.join(usb, "idVendor")):
        usb = os.path.dirname(usb)  # walk up from the tty/interface node to the USB device
    try:
        vid = open(os.path.join(usb, "idVendor")).read().strip()
        pid = open(os.path.join(usb, "idProduct")).read().strip()
    except OSError as e:
        raise SystemExit(f"cannot read the USB ID of {port}: {e}")
    if f"{vid}:{pid}" not in ALLOWED_USB_IDS:
        raise SystemExit(f"{port} is {vid}:{pid}, not the ESP32 ({sorted(ALLOWED_USB_IDS)}); refusing")
    print(f"{port}: USB {vid}:{pid} OK")


def esptool(port, *args, after="no_reset"):
    cmd = [sys.executable, ESPTOOL, "--chip", "esp32", "--port", port, "--after", after, *args]
    print("$", " ".join(cmd), flush=True)
    out = subprocess.run(cmd, capture_output=True, text=True)
    print(out.stdout[-1500:], out.stderr[-800:], flush=True)
    if out.returncode != 0:
        raise SystemExit(f"esptool failed ({out.returncode})")
    return out.stdout


def nvs_md5(port, tmp):
    path = os.path.join(tmp, "nvs.bin")
    esptool(port, "read_flash", hex(NVS_OFFSET), hex(NVS_SIZE), path)
    data = open(path, "rb").read()
    return hashlib.md5(data).hexdigest(), data


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", required=True)
    ap.add_argument("--parts-dir", default=".")
    a = ap.parse_args()
    port, pdir = a.port, os.path.abspath(a.parts_dir)
    check_usb_id(port)
    with open(os.path.join(pdir, "parts.txt")) as fh:
        parts = [(int(o, 16), os.path.join(pdir, f)) for o, f in (line.split() for line in fh if line.strip())]
    with tempfile.TemporaryDirectory() as tmp:
        print("== 1. chip, flash size, MAC")
        esptool(port, "flash_id")

        print("== 2. put a recognisable marker into NVS")
        marker = os.path.join(tmp, "marker.bin")
        open(marker, "wb").write(b"\xA5" * NVS_SIZE)
        esptool(port, "write_flash", hex(NVS_OFFSET), marker)
        before, _ = nvs_md5(port, tmp)
        print("NVS md5 with marker:", before)

        print("== 3. write the four parts separately (as Volco's Update will)")
        args = []
        for off, f in parts:
            args += [hex(off), f]
        esptool(port, "write_flash", *args)
        after, _ = nvs_md5(port, tmp)
        print("NVS md5 after writing parts:", after, "UNCHANGED" if after == before else "CHANGED  <-- FINDING")

        print("== 4. MD5 verify of every part (SPI_FLASH_MD5 via the stub)")
        esptool(port, "verify_flash", *args)

        print("== 5. let the app boot over the garbage NVS, capture 4 s of serial")
        esptool(port, "read_mac", after="hard_reset")
        import serial  # pyserial ships with PlatformIO's Python
        with serial.Serial(port, 115200, timeout=0.2) as s:
            end, buf = time.time() + 4, b""
            while time.time() < end:
                buf += s.read(4096)
        print(f"serial: {len(buf)} bytes, {buf.count(0xAA)} x 0xAA")
        print("serial text:", buf.decode("ascii", "replace")[:600])

        print("== 6. NVS after the app booted")
        booted, data = nvs_md5(port, tmp)
        print("NVS md5 after boot:", booted, "| first 32 bytes:", data[:32].hex())
        print("RECOVERED (rewritten by initArduino)" if booted != before else "NOT REWRITTEN  <-- FINDING")


if __name__ == "__main__":
    main()

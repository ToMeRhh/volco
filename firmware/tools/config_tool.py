#!/usr/bin/env python3
"""Reads or writes a Volco controller's config over USB serial or UDP (spec §5.3, §5.4).

For firmware work without the app (the app's Controller page does the same). Layouts are preset JSON files: the
protobuf JSON mapping of ControllerConfig, like proto/presets/diy-reference.json.

    PY=~/.platformio/penv/bin/python
    $PY firmware/tools/config_tool.py --port /dev/ttyUSB1 hello
    $PY firmware/tools/config_tool.py --port /dev/ttyUSB1 read [--out layout.json]
    $PY firmware/tools/config_tool.py --port /dev/ttyUSB1 write layout.json
    $PY firmware/tools/config_tool.py --port /dev/ttyUSB1 write layout.json --corrupt-crc   # expect BAD_CRC
    $PY firmware/tools/config_tool.py --port /dev/ttyUSB1 write layout.json --no-end        # expect no answer (timeout)
    $PY firmware/tools/config_tool.py --port /dev/ttyUSB1 write layout.json --total 1025    # expect TOO_LARGE
    $PY firmware/tools/config_tool.py --udp 192.168.1.50 --listen 12345 read                # from the PC set as pc_ip
    $PY firmware/tools/config_tool.py --selftest

A Wi-Fi change written over USB (while the stored Wi-Fi was on) is provisional: without a valid UDP frame from
Volco within 30 s of the reboot, the controller restores its previous Wi-Fi settings and reboots.

Serial ports go through hello_check's USB-ID guard. Opening a serial port resets the board, so the
tool waits for its boot Hello first. Every frame sent and received is printed in hex.
"""
import argparse
import importlib
import json
import os
import random
import socket
import sys
import tempfile
import time
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from hello_check import check_usb_id, frames, parse_hello  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PROTO_DIR = os.path.join(ROOT, "proto")
CHUNK = 240
MAX_BLOB = 1024
RESULTS = {0: "OK", 1: "BAD_SEQUENCE", 2: "BAD_CRC", 3: "TOO_LARGE", 4: "DECODE_FAILED", 5: "INVALID",
           6: "STORAGE_FAILED"}


def load_pb2():
    from grpc_tools import protoc
    tmp = tempfile.mkdtemp()
    if protoc.main(["protoc", f"-I{PROTO_DIR}", f"--python_out={tmp}", os.path.join(PROTO_DIR, "volco_config.proto")]):
        raise SystemExit("protoc failed")
    sys.path.insert(0, tmp)
    return importlib.import_module("volco_config_pb2")


def hexs(b):
    return " ".join(f"{x:02X}" for x in b)


def frame(payload):
    cs = 0
    for b in payload:
        cs ^= b
    return bytes([0xAA, len(payload), *payload, cs])


def write_burst(seq, blob, total=None, crc=None, end=True):
    total = len(blob) if total is None else total
    crc = zlib.crc32(blob) if crc is None else crc
    out = [frame(bytes([0x88, seq]) + total.to_bytes(2, "big") + crc.to_bytes(4, "big"))]
    for off in range(0, len(blob), CHUNK):
        out.append(frame(bytes([0x89, seq]) + off.to_bytes(2, "big") + blob[off:off + CHUNK]))
    if end:
        out.append(frame(bytes([0x8A, seq])))
    return out


# ── Links ──────────────────────────────────────────────────────────────────────────────────────
class SerialLink:
    def __init__(self, port):
        check_usb_id(port)
        import serial
        self.s = serial.Serial(port, 115200, timeout=0.05)
        self.buf = b""
        # Reset explicitly (as hello_check does), so the boot Hello is always there to wait for.
        self.s.dtr = False
        self.s.rts = True
        time.sleep(0.1)
        self.s.rts = False

    def send(self, data):
        self.s.write(data)
        self.s.flush()

    def read(self):
        return self.s.read(4096)


class UdpLink:
    def __init__(self, target, listen):
        host, _, port = target.partition(":")
        self.addr = (host, int(port or 12346))
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind(("0.0.0.0", listen))
        self.sock.settimeout(0.05)

    def send(self, data):
        self.sock.sendto(data, self.addr)

    def read(self):
        try:
            data, src = self.sock.recvfrom(2048)
            return data if src[0] == self.addr[0] else b""
        except socket.timeout:
            return b""


class Reader:
    """Collects frames from a link, printing each one."""

    def __init__(self, link):
        self.link = link
        self.pending = b""

    def send(self, data, label):
        print(f">> {label}: {hexs(data)}")
        self.link.send(data)

    def wait(self, want, seconds):
        """The first payload for which want(payload) is true, or None after `seconds`."""
        end = time.time() + seconds
        while time.time() < end:
            self.pending += self.link.read()
            consumed = 0
            for p, stop in frames_with_end(self.pending):
                consumed = stop
                if p[0] not in (0x02, 0x03):  # Alive and Slider are noise here
                    print(f"<< {hexs(p)}")
                if want(p):
                    self.pending = self.pending[stop:]
                    return p
            if consumed:
                self.pending = self.pending[consumed:]
        return None


def frames_with_end(data):
    """Like hello_check.frames, but also yields where each frame ends."""
    i = 0
    while i < len(data):
        if data[i] != 0xAA or i + 2 >= len(data):
            i += 1
            continue
        n = data[i + 1]
        end = i + 2 + n
        if n == 0 or end >= len(data):
            if end >= len(data):
                return  # maybe incomplete: keep it for the next read
            i += 1
            continue
        payload = data[i + 2:end]
        cs = 0
        for b in payload:
            cs ^= b
        if cs == data[end]:
            yield payload, end + 1
            i = end + 1
        else:
            i += 1


def is_hello(p):
    return p[0] == 0x01


def describe_hello(p):
    h = parse_hello(p)
    if not h:
        return f"unparsed Hello {hexs(p)}"
    state = {0: "none", 1: "stored", 2: "damaged"}.get(h["state"], h["state"])
    return (f"chip {h['chip']}, firmware {h['firmware']!r}, config {state} crc {h['crc']:08X}, "
            f"{h['sliders']} sliders, kinds {h['kinds']}")


# ── Commands ───────────────────────────────────────────────────────────────────────────────────
def boot_hello(r, serial):
    if not serial:
        return None
    h = r.wait(is_hello, 6)
    if h is None:
        sys.exit("no boot Hello")
    print("HELLO", describe_hello(h))
    return h


def cmd_hello(r, serial):
    if not serial:
        r.send(frame(bytes([0x81])), "HelloRequest")
        h = r.wait(is_hello, 3)
        if h is None:
            sys.exit("no Hello")
        print("HELLO", describe_hello(h))
    else:
        boot_hello(r, serial)


def do_read(r, pb2):
    seq = random.randrange(256)
    r.send(frame(bytes([0x87, seq])), f"ConfigRead seq {seq}")
    blob, total, crc = b"", None, None
    end = time.time() + 3
    while time.time() < end:
        p = r.wait(lambda p: p[0] == 0x05 and len(p) >= 10 and p[1] == seq, max(0.0, end - time.time()))
        if p is None:
            break
        t, off, c = int.from_bytes(p[2:4], "big"), int.from_bytes(p[4:6], "big"), int.from_bytes(p[6:10], "big")
        if total is None:
            total, crc = t, c
        if (t, c) != (total, crc) or off != len(blob):
            sys.exit(f"READ BAD: chunk at {off} (total {t}, crc {c:08X}) after {len(blob)} bytes of {total}, crc {crc:08X}")
        blob += bytes(p[10:])
        if len(blob) >= total:
            break
    if total is None:
        sys.exit("no ConfigData")
    if len(blob) != total or zlib.crc32(blob) != crc:
        sys.exit(f"READ BAD: {len(blob)}/{total} bytes, crc {zlib.crc32(blob):08X} vs {crc:08X}")
    print(f"READ OK: {total} bytes, crc {crc:08X}")
    if total == 0:
        return None, 0
    from google.protobuf import json_format
    cfg = pb2.ControllerConfig()
    cfg.ParseFromString(blob)
    return json_format.MessageToDict(cfg), crc


def cmd_read(r, serial, pb2, out):
    boot_hello(r, serial)
    cfg, _ = do_read(r, pb2)
    text = json.dumps(cfg, indent=2) if cfg is not None else "null"
    print(text)
    if out:
        open(out, "w").write(text + "\n")


def cmd_write(r, serial, pb2, path, a):
    from google.protobuf import json_format
    cfg = json_format.Parse(open(path).read(), pb2.ControllerConfig())
    blob = cfg.SerializeToString()
    crc = zlib.crc32(blob)
    print(f"blob {len(blob)} bytes, crc {crc:08X}")
    boot_hello(r, serial)
    seq = random.randrange(256)
    bad_crc = crc ^ 0xFFFFFFFF if a.corrupt_crc else None
    burst = write_burst(seq, blob, total=a.total, crc=bad_crc, end=not a.no_end)
    if a.total is not None and a.total > MAX_BLOB:
        burst = burst[:1]  # the Begin alone: the controller answers TOO_LARGE
    r.send(b"".join(burst), f"write burst seq {seq}, {len(burst)} frames")
    res = r.wait(lambda p: p[0] == 0x06 and len(p) >= 5 and p[1] == seq, 3)
    if res is None:
        print("RESULT none within 3 s")
        return
    code, item, field = res[2], res[3], res[4]
    print(f"RESULT {RESULTS.get(code, code)} item {item} field {field}")
    if code != 0:
        return
    h = r.wait(is_hello, 10)
    if h is None:
        sys.exit("no Hello after the reboot")
    info = parse_hello(h)
    print("HELLO", describe_hello(h))
    if info and info["state"] == 1 and info["crc"] == crc:
        print("WRITE OK: the controller runs the new config")
    else:
        sys.exit("WRITE MISMATCH: the Hello after the reboot doesn't report the new config")


def selftest(pb2):
    from google.protobuf import json_format
    cfg = json_format.Parse(open(os.path.join(PROTO_DIR, "presets", "diy-reference.json")).read(), pb2.ControllerConfig())
    blob = cfg.SerializeToString()
    golden = {}
    for line in open(os.path.join(ROOT, "firmware", "test", "golden", "frames.txt")):
        if line.strip() and not line.startswith("#"):
            name, *hx = line.split()
            golden[name] = bytes(int(x, 16) for x in hx)
    small = b"".join(write_burst(9, bytes([0x32, 0x02, 0x08, 0x64])))
    assert small == golden["config_write_begin_seq9"] + golden["config_write_chunk_seq9"] + golden["config_write_end_seq9"]
    assert frame(bytes([0x87, 7])) == golden["config_read_seq7"]
    burst = b"".join(write_burst(3, blob))
    got = [p for p in frames(burst)]
    assert got[0][0] == 0x88 and got[-1][0] == 0x8A
    assert b"".join(bytes(p[4:]) for p in got[1:-1]) == blob
    assert parse_hello(golden["hello_led_hardware"][2:-1])["crc"] == 0x12345678
    assert parse_hello(golden["hello_old_format"][2:-1]) is None
    print(f"SELFTEST OK ({len(blob)}-byte DIY blob, crc {zlib.crc32(blob):08X})")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port")
    ap.add_argument("--udp", help="board IP[:port], default port 12346")
    ap.add_argument("--listen", type=int, default=12345, help="local UDP port (the config's pc_port)")
    ap.add_argument("--selftest", action="store_true")
    sub = ap.add_subparsers(dest="cmd")
    sub.add_parser("hello")
    rd = sub.add_parser("read")
    rd.add_argument("--out")
    wr = sub.add_parser("write")
    wr.add_argument("json")
    wr.add_argument("--corrupt-crc", action="store_true")
    wr.add_argument("--no-end", action="store_true")
    wr.add_argument("--total", type=int)
    a = ap.parse_args()
    pb2 = load_pb2()
    if a.selftest:
        selftest(pb2)
        return
    if bool(a.port) == bool(a.udp) or not a.cmd:
        ap.error("give a command and exactly one of --port or --udp")
    link = SerialLink(a.port) if a.port else UdpLink(a.udp, a.listen)
    r = Reader(link)
    serial = bool(a.port)
    if a.cmd == "hello":
        cmd_hello(r, serial)
    elif a.cmd == "read":
        cmd_read(r, serial, pb2, a.out)
    else:
        cmd_write(r, serial, pb2, a.json, a)


if __name__ == "__main__":
    main()

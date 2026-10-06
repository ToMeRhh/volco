#!/usr/bin/env python3
"""Generates firmware/test/golden/config.txt: controller config blobs and what both codecs must
make of them (spec §12.1). Read by firmware/test/test_config and tests/Volco.Core.Tests.

The expected decode values, validation triples (code item field) and CRCs written here are the
spec's oracle (§6.1, §6.4, §6.5), stated by hand per case; only the blob bytes are computed.

Run with PlatformIO's Python (it has protobuf and grpcio-tools):
    ~/.platformio/penv/bin/python firmware/tools/gen_config_vectors.py
Commit the output. CI re-runs this and fails if config.txt is stale.
"""
import importlib
import os
import sys
import tempfile
import zlib

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PROTO_DIR = os.path.join(ROOT, "proto")
OUT = os.path.join(ROOT, "firmware", "test", "golden", "config.txt")


def load_pb2():
    from grpc_tools import protoc

    tmp = tempfile.mkdtemp()
    rc = protoc.main(["protoc", f"-I{PROTO_DIR}", f"--python_out={tmp}", os.path.join(PROTO_DIR, "volco_config.proto")])
    if rc != 0:
        raise SystemExit("protoc failed")
    sys.path.insert(0, tmp)
    return importlib.import_module("volco_config_pb2")


pb = load_pb2()
lines = []
OK = (0, 255, 255)


def ip(a, b, c, d):
    return (a << 24) | (b << 16) | (c << 8) | d


def blob(m):
    return m.SerializeToString(deterministic=True)


def hexs(data):
    return data.hex() if data else "-"


def diy():
    m = pb.ControllerConfig(preset="diy-reference")
    for p in (33, 35, 32, 36, 34):
        m.sliders.add(pin=p)
    b = m.buttons.add(pin=4)
    b.led.pin = 12
    b.led.active_low = True
    b = m.buttons.add(pin=14)
    b.led.pin = 21
    b.led.active_low = True
    b = m.buttons.add(pin=5)
    b.led_pair.pin_a = 18
    b.led_pair.pin_b = 19
    b.led_pair.active_low = False
    m.i2c0.sda, m.i2c0.scl = 25, 26
    m.i2c1.sda, m.i2c1.scl = 27, 13
    return m


def udp_ok(m):
    m.udp.enabled = True
    m.udp.ssid = "net"
    m.udp.password = ""
    m.udp.pc_ip = ip(192, 168, 1, 10)


def screen(m, i, bus, address):
    s = m.buttons[i].screen
    s.bus = bus
    s.address = address


def maximal():
    m = pb.ControllerConfig(preset="p" * 24)
    for _ in range(16):
        m.sliders.add(pin=39)
    for _ in range(32):
        b = m.buttons.add(pin=39)
        b.led_pair.pin_a, b.led_pair.pin_b, b.led_pair.active_low = 39, 39, True
    for bus in (m.i2c0, m.i2c1, m.i2c2, m.i2c3):
        bus.sda, bus.scl, bus.clock_hz = 39, 39, 1000000
    u = m.udp
    u.enabled, u.ssid, u.password = True, "a" * 32, "a" * 64
    u.pc_ip, u.pc_port, u.local_port = 0xFFFFFFFF, 65535, 65535
    s = m.screens
    s.brightness, s.transition_ms, s.scroll_hold_ms, s.scroll_speed_px_s = 255, 2000, 10000, 200
    s.scroll_end_hold_ms, s.dim_enabled, s.dim_after_s, s.dim_brightness = 10000, True, 86400, 255
    s.off_enabled, s.off_after_s, s.pixel_shift_enabled, s.pixel_shift_every_s = True, 86400, True, 86400
    s.wake_swallows_press = True
    i = m.input
    i.debounce_ms, i.slider_interval_ms, i.filter_weight_pct, i.slider_deadband = 200, 100, 100, 500
    return m


def decode_ok(name, data, **expect):
    kv = " ".join(f"{k.replace('__', '.')}={v}" for k, v in expect.items())
    lines.append(f"decode-ok {name} {hexs(data)} {kv}")


def decode_fail(name, hex_bytes):
    lines.append(f"decode-fail {name} {hex_bytes}")


def validate(name, m, triple):
    lines.append(f"validate {name} {hexs(blob(m))} {triple[0]} {triple[1]} {triple[2]}")


def case(name, mutate, triple):
    m = diy()
    mutate(m)
    validate(name, m, triple)


def bad(item, field):
    return (5, item, field)


# ---- decode-ok -------------------------------------------------------------------------------
lines.append("# decode-ok <name> <hex> key=value ...")
decode_ok("diy_reference", blob(diy()), sliders=5, buttons=3, preset="diy-reference",
          buttons__0__kind=1, screens__brightness=255)
decode_ok("empty", b"", size=0, sliders=0, buttons=0, preset="", screens__brightness=255,
          screens__dim_enabled=1, screens__transition_ms=360)
decode_ok("screen_default", bytes.fromhex("12022200"), buttons=1, buttons__0__kind=3,
          buttons__0__screen__bus=0, buttons__0__screen__address=60)
decode_ok("brightness_only", bytes.fromhex("32020864"), screens__brightness=100,
          screens__dim_enabled=1, screens__transition_ms=360)
decode_ok("unknown_field", bytes.fromhex("98060132020864"), screens__brightness=100)
mx = blob(maximal())
assert len(mx) == 683, len(mx)  # 663 bytes measured in stage 1 with two buses; buses 2 and 3 add 20
decode_ok("maximal", mx, size=683, sliders=16, buttons=32)

# ---- decode-fail -----------------------------------------------------------------------------
lines.append("# decode-fail <name> <hex>")
decode_fail("truncated", "1202")
decode_fail("bad_varint", "0a0c08ffffffffffffffffffff01")
decode_fail("ssid_33", "2a231221" + "61" * 33)
# One byte over each remaining text limit (spec §6.1): preset max 24 bytes, password max 64.
decode_fail("preset_25", "4219" + "70" * 25)               # field 8 (preset), 25 bytes
decode_fail("password_65", "2a43" + "1a41" + "61" * 65)    # udp (5) { password (3): 65 bytes }
decode_fail("ssid_multibyte_34", "2a241222" + "c3a9" * 17)
decode_fail("buttons_33", "1200" * 33)
decode_fail("sliders_17", "0a00" * 17)
decode_fail("nul_in_preset", "4203610062")
decode_fail("nul_in_ssid", "2a051203610062")
decode_fail("bad_utf8", "4202c328")

# ---- validate --------------------------------------------------------------------------------
lines.append("# validate <name> <hex> <code> <item> <field>")
case("valid_diy", lambda m: None, OK)
case("valid_two_screens", lambda m: (screen(m, 0, 0, 0x3C), screen(m, 1, 0, 0x3D)), OK)

case("slider_missing_pin", lambda m: m.sliders[2].ClearField("pin"), bad(2, 1))
case("slider_reserved", lambda m: setattr(m.sliders[0], "pin", 6), bad(0, 1))
case("slider_nonexistent", lambda m: setattr(m.sliders[0], "pin", 24), bad(0, 1))
case("slider_pin_64", lambda m: setattr(m.sliders[0], "pin", 64), bad(0, 1))
case("slider_pin_max", lambda m: setattr(m.sliders[0], "pin", 4294967295), bad(0, 1))
case("slider_not_adc", lambda m: setattr(m.sliders[1], "pin", 23), bad(1, 1))
case("slider_adc2_with_udp", lambda m: (setattr(m.sliders[0], "pin", 25), udp_ok(m)), bad(0, 1))
case("slider_adc2_without_udp", lambda m: setattr(m.sliders[0], "pin", 25), OK)
case("dup_two_sliders", lambda m: setattr(m.sliders[1], "pin", 33), bad(1, 1))

case("button_missing_pin", lambda m: m.buttons[1].ClearField("pin"), bad(1, 2))
case("button_reserved", lambda m: setattr(m.buttons[0], "pin", 1), bad(0, 2))
case("button_nonexistent", lambda m: setattr(m.buttons[0], "pin", 20), bad(0, 2))
case("button_input_only_ok", lambda m: setattr(m.buttons[0], "pin", 39), OK)
case("led_missing_pin", lambda m: m.buttons[0].led.ClearField("pin"), bad(0, 3))
case("led_input_only", lambda m: setattr(m.buttons[0].led, "pin", 39), bad(0, 3))
case("led_reserved", lambda m: setattr(m.buttons[0].led, "pin", 10), bad(0, 3))
case("pair_a_missing", lambda m: m.buttons[2].led_pair.ClearField("pin_a"), bad(2, 4))
case("pair_b_reserved", lambda m: setattr(m.buttons[2].led_pair, "pin_b", 7), bad(2, 5))
case("pair_b_equals_a", lambda m: setattr(m.buttons[2].led_pair, "pin_b", 18), bad(2, 5))
case("screen_bad_bus", lambda m: screen(m, 0, 4, 0x3C), bad(0, 6))
case("valid_screen_bus2",
     lambda m: (screen(m, 0, 2, 0x3C), setattr(m.i2c2, "sda", 16), setattr(m.i2c2, "scl", 17)), OK)
case("valid_screens_all_buses",
     lambda m: (screen(m, 0, 1, 0x3C), screen(m, 1, 2, 0x3C), screen(m, 2, 3, 0x3C),
                setattr(m.i2c2, "sda", 16), setattr(m.i2c2, "scl", 17),
                setattr(m.i2c3, "sda", 22), setattr(m.i2c3, "scl", 23)), OK)
case("screen_bad_address", lambda m: screen(m, 0, 0, 0x3E), bad(0, 7))
case("screen_duplicate", lambda m: (screen(m, 0, 0, 0x3C), screen(m, 1, 0, 0x3C)), bad(1, 7))
case("screen_third_on_bus",
     lambda m: (screen(m, 0, 0, 0x3C), screen(m, 1, 0, 0x3D), screen(m, 2, 0, 0x3C)), bad(2, 7))

case("i2c_missing_bus", lambda m: (screen(m, 0, 1, 0x3C), m.ClearField("i2c1")), bad(1, 8))
case("i2c_missing_scl", lambda m: (screen(m, 0, 0, 0x3C), m.i2c0.ClearField("scl")), bad(0, 9))
case("i2c_sda_eq_scl", lambda m: (screen(m, 0, 0, 0x3C), setattr(m.i2c0, "scl", 25)), bad(0, 9))
case("i2c_input_only", lambda m: (screen(m, 0, 0, 0x3C), setattr(m.i2c0, "sda", 39)), bad(0, 9))
case("i2c_clock_low", lambda m: (screen(m, 0, 0, 0x3C), setattr(m.i2c0, "clock_hz", 99999)), bad(0, 10))
case("i2c_clock_high", lambda m: (screen(m, 0, 0, 0x3C), setattr(m.i2c0, "clock_hz", 1000001)), bad(0, 10))
case("i2c_unused_bus_ignored", lambda m: setattr(m.i2c1, "sda", 1), OK)
case("i2c_missing_bus2", lambda m: screen(m, 0, 2, 0x3C), bad(2, 8))
case("i2c_missing_bus3", lambda m: screen(m, 0, 3, 0x3C), bad(3, 8))
case("order_bus2_before_bus3", lambda m: (screen(m, 0, 3, 0x3C), screen(m, 1, 2, 0x3C)), bad(2, 8))
case("dup_led_i2c2",
     lambda m: (screen(m, 0, 2, 0x3C), setattr(m.i2c2, "sda", 16), setattr(m.i2c2, "scl", 17),
                setattr(m.buttons[1].led, "pin", 16)), bad(2, 9))

case("dup_slider_button", lambda m: setattr(m.buttons[0], "pin", 33), bad(0, 2))
case("dup_button_led", lambda m: setattr(m.buttons[1].led, "pin", 4), bad(1, 3))
case("dup_led_i2c", lambda m: (screen(m, 0, 0, 0x3C), setattr(m.buttons[1].led, "pin", 25)), bad(0, 9))
case("dup_unused_bus_ok", lambda m: setattr(m.buttons[0], "pin", 27), OK)

case("udp_ok", udp_ok, OK)
case("udp_ssid_empty", lambda m: (udp_ok(m), setattr(m.udp, "ssid", "")), bad(255, 11))
case("ssid_multibyte_32", lambda m: (udp_ok(m), setattr(m.udp, "ssid", "é" * 16)), OK)
case("udp_password_7", lambda m: (udp_ok(m), setattr(m.udp, "password", "1234567")), bad(255, 12))
case("udp_password_8_ok", lambda m: (udp_ok(m), setattr(m.udp, "password", "12345678")), OK)
case("udp_password_64_ok", lambda m: (udp_ok(m), setattr(m.udp, "password", "p" * 64)), OK)
case("udp_ip_missing", lambda m: (udp_ok(m), m.udp.ClearField("pc_ip")), bad(255, 13))
case("udp_ip_zero", lambda m: (udp_ok(m), setattr(m.udp, "pc_ip", 0)), bad(255, 13))
case("udp_ip_broadcast", lambda m: (udp_ok(m), setattr(m.udp, "pc_ip", 0xFFFFFFFF)), bad(255, 13))
case("udp_ip_loopback", lambda m: (udp_ok(m), setattr(m.udp, "pc_ip", ip(127, 0, 0, 1))), bad(255, 13))
case("udp_ip_multicast", lambda m: (udp_ok(m), setattr(m.udp, "pc_ip", ip(224, 0, 0, 1))), bad(255, 13))
case("udp_ip_multicast_top", lambda m: (udp_ok(m), setattr(m.udp, "pc_ip", ip(239, 255, 255, 250))), bad(255, 13))
case("udp_ip_240_ok", lambda m: (udp_ok(m), setattr(m.udp, "pc_ip", ip(240, 0, 0, 1))), OK)
case("udp_pc_port_zero", lambda m: (udp_ok(m), setattr(m.udp, "pc_port", 0)), bad(255, 14))
case("udp_local_port_big", lambda m: (udp_ok(m), setattr(m.udp, "local_port", 65536)), bad(255, 14))
case("udp_disabled_incomplete_ok", lambda m: (setattr(m.udp, "enabled", False), setattr(m.udp, "ssid", "")), OK)

SCREEN_RANGES = [("brightness", 1, 0, 255), ("transition_ms", 2, 0, 2000), ("scroll_hold_ms", 3, 0, 10000),
                 ("scroll_speed_px_s", 4, 5, 200), ("scroll_end_hold_ms", 5, 0, 10000),
                 ("dim_after_s", 7, 10, 86400), ("dim_brightness", 8, 0, 255), ("off_after_s", 10, 10, 86400),
                 ("pixel_shift_every_s", 12, 10, 86400)]
INPUT_RANGES = [("debounce_ms", 1, 1, 200), ("slider_interval_ms", 2, 1, 100), ("filter_weight_pct", 3, 1, 100),
                ("slider_deadband", 4, 0, 500)]
for sub, field_code, table in (("screens", 15, SCREEN_RANGES), ("input", 16, INPUT_RANGES)):
    for fname, num, lo, hi in table:
        if lo > 0:
            case(f"range_{sub}_{fname}_below", lambda m, s=sub, f=fname, v=lo - 1: setattr(getattr(m, s), f, v),
                 bad(num, field_code))
        case(f"range_{sub}_{fname}_above", lambda m, s=sub, f=fname, v=hi + 1: setattr(getattr(m, s), f, v),
             bad(num, field_code))
for which in ("min", "max"):
    def all_at(m, which=which):
        for sub, table in (("screens", SCREEN_RANGES), ("input", INPUT_RANGES)):
            for fname, _num, lo, hi in table:
                setattr(getattr(m, sub), fname, lo if which == "min" else hi)
    case(f"range_all_{which}_ok", all_at, OK)

case("order_slider_before_udp",
     lambda m: (setattr(m.sliders[0], "pin", 6), udp_ok(m), setattr(m.udp, "ssid", "")), bad(0, 1))
case("order_button_pin_before_led",
     lambda m: (setattr(m.buttons[0], "pin", 1), setattr(m.buttons[0].led, "pin", 39)), bad(0, 2))
case("order_sliders_before_buttons",
     lambda m: (setattr(m.buttons[0], "pin", 6), setattr(m.sliders[4], "pin", 23)), bad(4, 1))
case("order_dup_before_range",
     lambda m: (setattr(m.buttons[0], "pin", 33), setattr(m.screens, "brightness", 256)), bad(0, 2))
case("order_i2c_before_dup",
     lambda m: (screen(m, 0, 0, 0x3C), setattr(m.i2c0, "clock_hz", 1), setattr(m.buttons[1], "pin", 33)),
     bad(0, 10))
case("order_screens_before_input",
     lambda m: (setattr(m.screens, "transition_ms", 2001), setattr(m.input, "debounce_ms", 0)), bad(2, 15))
case("order_range_field_order",
     lambda m: (setattr(m.screens, "dim_brightness", 256), setattr(m.screens, "brightness", 256)), bad(1, 15))

# ---- crc -------------------------------------------------------------------------------------
lines.append("# crc <name> <hex> <crc32 hex>")
for name, data in (("digits", b"123456789"), ("empty", b""), ("diy_reference", blob(diy()))):
    lines.append(f"crc {name} {hexs(data)} {zlib.crc32(data) & 0xFFFFFFFF:08x}")
assert "crc digits 313233343536373839 cbf43926" in lines

# hasNulInStrings (firmware/lib/volco_config) and ControllerConfigCodec.CheckLimits scan exactly
# these string fields. A string anywhere else (e.g. inside a repeated message) must be added there
# first, so the generator refuses to run until it is.
SCANNED = {"volco.config.ControllerConfig.preset", "volco.config.Udp.ssid", "volco.config.Udp.password"}


def string_fields(desc, seen=None):
    seen = seen if seen is not None else set()
    if desc.full_name in seen:
        return set()
    seen.add(desc.full_name)
    out = set()
    for f in desc.fields:
        if f.type == f.TYPE_STRING:
            out.add(f.full_name)
        elif f.type == f.TYPE_MESSAGE:
            out |= string_fields(f.message_type, seen)
    return out


found = string_fields(pb.ControllerConfig.DESCRIPTOR)
if found != SCANNED:
    raise SystemExit(f"string fields changed: {sorted(found ^ SCANNED)} - teach hasNulInStrings and CheckLimits first")

header = [
    "# Generated by firmware/tools/gen_config_vectors.py; do not edit. Shared by",
    "# firmware/test/test_config and tests/Volco.Core.Tests/Controller (spec §12.1).",
]
with open(OUT, "w", newline="\n") as fh:
    fh.write("\n".join(header + lines) + "\n")
print(f"wrote {OUT}: {sum(1 for l in lines if not l.startswith('#'))} cases")

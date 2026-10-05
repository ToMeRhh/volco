# PlatformIO post-script (spec §9.1): copies the four flash parts into $BUILD_DIR/dist and writes
# dist/firmware-manifest.json, which the app installs from. The offsets come from the platform
# (FLASH_EXTRA_IMAGES, ESP32_APP_OFFSET) and the NVS range from the partition table; nothing is
# hard-coded. The build fails if a part would overlap NVS: Update must never touch the config.
import hashlib
import json
import os
import shutil
import sys
import tempfile

try:
    Import("env")  # noqa: F821 (provided by PlatformIO)
except NameError:  # run directly: python firmware/scripts/manifest.py --selftest
    env = None


def parse_size(text):
    text = str(text).strip()
    if isinstance(text, int):
        return text
    mult = 1
    if text[-1:].upper() == "K":
        mult, text = 1024, text[:-1]
    elif text[-1:].upper() == "M":
        mult, text = 1024 * 1024, text[:-1]
    return int(text, 0) * mult


def nvs_range(csv_path):
    """The (offset, size) of the nvs partition, computing blank offsets like the platform does."""
    next_offset = 0x9000  # the table itself lives at 0x8000-0x8FFF
    with open(csv_path) as fp:
        for line in fp:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            tokens = [t.strip() for t in line.split(",")]
            if len(tokens) < 5:
                continue
            bound = 0x10000 if tokens[1] in ("0", "app") else 4
            offset = parse_size(tokens[3]) if tokens[3] else (next_offset + bound - 1) & ~(bound - 1)
            size = parse_size(tokens[4])
            if tokens[0] == "nvs":
                return offset, size
            next_offset = offset + size
    raise SystemExit(f"manifest.py: no nvs partition in {csv_path}")


def check_overlap(name, offset, size, nvs_offset, nvs_size):
    """Fails the build when a part would write into NVS (spec §9.1)."""
    if offset < nvs_offset + nvs_size and nvs_offset < offset + size:
        raise SystemExit(f"manifest.py: {name} at 0x{offset:X}+{size} overlaps NVS 0x{nvs_offset:X}+0x{nvs_size:X}")


def selftest():
    with tempfile.NamedTemporaryFile("w", suffix=".csv", delete=False) as fp:
        fp.write("# Name, Type, SubType, Offset, Size, Flags\nnvs, data, nvs, 0x9000, 0x5000,\notadata, data, ota, , 0x2000,\n"
                 "app0, app, ota_0, , 1280K,\n")
    assert nvs_range(fp.name) == (0x9000, 0x5000)
    os.unlink(fp.name)
    check_overlap("boot_app0", 0xE000, 0x2000, 0x9000, 0x5000)   # adjacent: fine
    check_overlap("partitions", 0x8000, 0x1000, 0x9000, 0x5000)
    for offset, size in ((0x8000, 0x1001), (0xDFFF, 1), (0x9000, 0x10), (0x4000, 0x10000)):
        try:
            check_overlap("part", offset, size, 0x9000, 0x5000)
        except SystemExit:
            continue
        raise AssertionError(f"0x{offset:X}+0x{size:X} not caught")
    print("manifest.py SELFTEST OK")


def write_manifest(target, source, env):
    build_dir = env.subst("$BUILD_DIR")
    dist = os.path.join(build_dir, "dist")
    os.makedirs(dist, exist_ok=True)

    names = {"bootloader": "bootloader", "partitions": "partitions", "boot_app0": "boot_app0"}
    parts = []
    for offset, path in env.get("FLASH_EXTRA_IMAGES", []):
        path = env.subst(path)
        base = os.path.basename(path)
        name = next((n for k, n in names.items() if base.startswith(k)), os.path.splitext(base)[0])
        parts.append((name, path, parse_size(offset)))
    parts.append(("app", env.subst("$BUILD_DIR/${PROGNAME}.bin"), parse_size(env.subst("$ESP32_APP_OFFSET") or "0x10000")))

    nvs_offset, nvs_size = nvs_range(env.subst("$PARTITIONS_TABLE_CSV"))
    entries = []
    for name, path, offset in sorted(parts, key=lambda p: p[2]):
        data = open(path, "rb").read()
        check_overlap(name, offset, len(data), nvs_offset, nvs_size)
        file = "firmware.bin" if name == "app" else f"{name}.bin"
        shutil.copyfile(path, os.path.join(dist, file))
        entries.append({"name": name, "file": file, "offset": offset, "size": len(data),
                        "md5": hashlib.md5(data).hexdigest()})

    version = os.environ.get("VOLCO_FW_VERSION", "").strip() or "dev"
    manifest = {
        "chip": "esp32",
        "version": version,
        "displayVersion": os.environ.get("VOLCO_FW_DISPLAY", "").strip() or version,
        "parts": entries,
        "nvs": {"offset": nvs_offset, "size": nvs_size},
    }
    with open(os.path.join(dist, "firmware-manifest.json"), "w") as fp:
        json.dump(manifest, fp, indent=2)
        fp.write("\n")
    print(f"Firmware manifest: {dist} (version {version}, {len(entries)} parts, NVS 0x{nvs_offset:X}+0x{nvs_size:X})")


if env is not None:
    env.AddPostAction("$BUILD_DIR/${PROGNAME}.bin", write_manifest)
elif "--selftest" in sys.argv:
    selftest()

#!/usr/bin/env python3
"""Checks the firmware build id script (spec §9.2): it prints 12 lowercase hex digits, the same on
every run, and its path list covers every directory platformio.ini builds from (src_dir, lib_dir,
extra_scripts, custom_nanopb_protos). Run from anywhere inside the repo with any Python 3."""
import configparser
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))  # firmware/tools: deliberately outside the hashed firmware/scripts
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
SCRIPT = os.path.join(ROOT, "firmware", "scripts", "firmware_id.sh")


def run(*args):
    return subprocess.run(["bash", SCRIPT, *args], check=True, capture_output=True, text=True, cwd=ROOT).stdout


def main():
    first, second = run().strip(), run().strip()
    if not re.fullmatch(r"[0-9a-f]{12}", first):
        sys.exit(f"firmware_id.sh printed {first!r}, not 12 hex digits")
    if first != second:
        sys.exit(f"firmware_id.sh is not stable: {first} then {second}")
    listed = run("--list").split()

    ini = configparser.ConfigParser(interpolation=None)
    ini.read(os.path.join(ROOT, "firmware", "platformio.ini"))
    referenced = {"firmware/src", "firmware/lib"}  # PlatformIO's defaults when src_dir/lib_dir are unset
    for section in ini.sections():
        for key in ("src_dir", "lib_dir", "extra_scripts", "custom_nanopb_protos"):
            for token in ini.get(section, key, fallback="").split():
                token = re.sub(r"^(pre:|post:|\+<|-<)", "", token).rstrip(">")
                path = os.path.relpath(os.path.normpath(os.path.join(ROOT, "firmware", token)), ROOT)
                referenced.add(path.replace(os.sep, "/"))
    missing = [r for r in sorted(referenced) if not any(r == p or r.startswith(p + "/") for p in listed)]
    if missing:
        sys.exit(f"firmware_id.sh doesn't hash: {missing}")
    print(f"firmware id {first} ({len(listed)} paths) OK")


if __name__ == "__main__":
    main()

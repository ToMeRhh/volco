# PlatformIO pre-script: the firmware build id Hello reports (spec §9.2). CI sets VOLCO_FW_VERSION
# to the source hash from firmware_id.sh; without it main.cpp falls back to "dev" (spec §9.4).
import os
import re

Import("env")  # noqa: F821 (provided by PlatformIO)

version = os.environ.get("VOLCO_FW_VERSION", "").strip()
if version:
    if len(version) > 32 or not re.fullmatch(r"[\x21-\x7e]+", version) or '"' in version or "\\" in version:
        raise SystemExit(f"VOLCO_FW_VERSION must be 1-32 printable ASCII characters without quotes: {version!r}")
    env.Append(CPPDEFINES=[("VOLCO_FW_VERSION", env.StringifyMacro(version))])  # noqa: F821
    print(f"Volco firmware build id: {version}")

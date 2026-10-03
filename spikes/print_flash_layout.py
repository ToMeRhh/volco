# PlatformIO extra script (spike only): prints what a flasher must write and where.
Import("env")  # noqa: F821  (provided by PlatformIO)


def _print_layout(source, target, env):
    print("SPIKE-LAYOUT BEGIN")
    for offset, path in env.get("FLASH_EXTRA_IMAGES", []):
        print(f"SPIKE-LAYOUT part {env.subst(offset)} {env.subst(path)}")
    print(f"SPIKE-LAYOUT app {env.subst(env.get('ESP32_APP_OFFSET', '?'))} {env.subst('$BUILD_DIR/${PROGNAME}.bin')}")
    print(f"SPIKE-LAYOUT partitions_csv {env.subst(env.get('PARTITIONS_TABLE_CSV', '?'))}")
    print("SPIKE-LAYOUT END")


env.AddPostAction("$BUILD_DIR/${PROGNAME}.bin", _print_layout)  # noqa: F821

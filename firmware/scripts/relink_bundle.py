# PlatformIO post-script: the LGPL 2.1 relink bundle (spec §14, point 3, option C). The firmware
# statically links the Arduino core for the ESP32; recipients of a release must be able to relink
# it with a modified core. This copies every link input built under $BUILD_DIR (Volco's own code,
# nanopb, U8g2, and the core's Arduino libraries as built for this firmware) and the exact link
# command into $BUILD_DIR/relink/. The core's precompiled ESP-IDF libraries and linker scripts stay
# where PlatformIO installs them (the command names them under ~/.platformio/packages).
import os
import shutil

Import("env")  # noqa: F821 (provided by PlatformIO)

README = """Relinking the Volco controller firmware (GNU LGPL 2.1, section 6)

The firmware statically links the Arduino core for the ESP32 (arduino-esp32 3.3.8, LGPL 2.1).
objects/ holds every link input that was compiled for this firmware: Volco's own code, nanopb,
U8g2, and the core's Arduino code (libFrameworkArduino.a and the core libraries Wire, WiFi,
Network, SPI and Preferences). link-command.txt is the exact command that linked them.

To relink the firmware with a modified core:
1. Install PlatformIO with the pioarduino platform 55.03.38 (Arduino core 3.3.8), so the paths
   under $HOME/.platformio/packages in link-command.txt exist, and apply your changes to the core.
2. Rebuild what you changed (e.g. libFrameworkArduino.a, by building any PlatformIO esp32dev
   Arduino project) and replace the matching files in objects/.
3. From this folder, run: sh link-command.txt   (its paths start at $HOME/.platformio; adjust them
   if PlatformIO lives elsewhere, and put the xtensa toolchain's bin/ on PATH). It writes
   objects/firmware.elf.
4. Convert it with the build's own flags:
   esptool --chip esp32 elf2image --flash-mode dio --flash-freq 40m --flash-size 4MB \
     --elf-sha256-offset 0xb0 -o firmware.bin objects/firmware.elf
   (Relinking the unchanged objects this way reproduces the shipped firmware.bin byte for byte.)
5. Flash firmware.bin at 0x10000, with bootloader.bin, partitions.bin and boot_app0.bin from this
   zip at the offsets in firmware-manifest.json (e.g. esptool write-flash).
"""


def write_bundle(target, source, env):
    project = env.subst("$PROJECT_DIR")
    build = env.subst("$BUILD_DIR")
    prefixes = sorted({build.rstrip("/") + "/", os.path.relpath(build, project).rstrip("/") + "/"}, key=len, reverse=True)
    out = os.path.join(build, "relink")
    shutil.rmtree(out, ignore_errors=True)
    os.makedirs(os.path.join(out, "objects"))

    # target/source are the link step's own: the .elf and every input the linker was given.
    command = env.subst("$LINKCOM", target=target, source=source)
    tokens = []
    for token in command.split():
        flag, path = ("-L", token[2:]) if token.startswith("-L") else ("", token)
        prefix = next((p for p in prefixes if path.startswith(p) or path + "/" == p), None)
        if prefix is None:
            tokens.append(token)
            continue
        rel = os.path.relpath(path if os.path.isabs(path) else os.path.join(project, path), build)
        src = os.path.join(build, rel)
        if os.path.isfile(src) and not src.endswith(".elf"):
            dst = os.path.join(out, "objects", rel)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copyfile(src, dst)
        tokens.append(flag + os.path.normpath(os.path.join("objects", rel)))
    text = " ".join(tokens)
    for p in prefixes:  # paths inside options, e.g. -Wl,-Map=".pio/build/esp32/firmware.map"
        text = text.replace(p, "objects/")
    # $HOME, not the build machine's home: run it with sh (README step 3).
    with open(os.path.join(out, "link-command.txt"), "w") as fp:
        fp.write(text.replace(os.path.expanduser("~"), "$HOME") + "\n")
    with open(os.path.join(out, "README-relink.txt"), "w") as fp:
        fp.write(README)
    print(f"Relink bundle: {out}")


# On the link step itself, so $LINKCOM expands with its real inputs. CI builds are always fresh, so
# the bundle is always written there; locally it is rewritten whenever the firmware relinks.
env.AddPostAction("$BUILD_DIR/${PROGNAME}.elf", write_bundle)  # noqa: F821

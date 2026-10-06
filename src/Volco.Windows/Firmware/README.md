# Firmware

CI puts the controller firmware here (`esp32/`: the four parts and `firmware-manifest.json`, from the
`firmware-esp32` artifact) and embeds it in `Volco.exe`; `-p:RequireFirmware=true` makes the build fail without it
or with a manifest from another build. Local builds have none, so they never offer a firmware update.

To try a local firmware build in a local app build, copy `firmware/.pio/build/esp32/dist/*` here as `esp32/`.

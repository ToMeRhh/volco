# Controller config: manual hardware checklist

From spec §12.7 (`docs/superpowers/specs/2026-10-03-controller-config-design.md`). Each item says which stage proves it. Record the date, the build, the board and the result next to each item.

**Setup:** Volco built by CI from the branch under test, on Windows, with the DIY reference board on USB. Firmware flashed with `pio run -e esp32 -t upload` until stage 6 brings Install.

## Stage 5 (Controller page)

- [x] **2. Apply the DIY reference.** On the Controller page: Set up (or Set up from scratch) → Start from: DIY reference → Save to controller. Expect:
  - "Sending… → Restarting… → Saved. The controller restarted with the new setup."
  - Start test: all 5 sliders move their bars, all 3 buttons light their lamps.
  - The LED choices light the LEDs (pair: A and B).
  - Volume and mute don't change while testing.
  - Stop test.
- [x] **3. Normal mode.** Volume and mute work, and the actions are unchanged.
- [x] **4. Change a pin.** Move a slider or button to a free pin (rewire it), then save. The old pin no longer reacts and the new one does.
- [x] **6. Wi-Fi.**
  - Advanced → Use Wi-Fi with a wrong password → save. It still works over USB.
  - Fix the password → save → set Volco's connection to Wi-Fi (UDP). It connects.
  - Change a behaviour setting and save over UDP. The save is confirmed after the reboot.
- [x] **8. Unplug during a save.** Unplug right after Save. After reconnecting, the banner reports correctly whether the old or the new setup is on the controller.
- [x] **12. Screens.** On the screen board: a layout with screens, made on the page (bus, address, SDA/SCL). The screens render. A behaviour change (brightness) takes effect after the save.
- [x] **13. Reboot with the COM port open.** A save over USB: no "Connection lost", and the save is confirmed.

## Stage 6 (install and CI)

- [x] 1. Install on a blank ESP32 → Hello, "Set up your controller".
- [x] 5. Update firmware (a second build id) → the config survives (same CRC, no banner).
- [x] 5a. Flash a `dev` build with `pio run -t upload` → the config survives; "Development firmware" with no update indicator; Install brings back the bundled build.
- [x] 5b. Two consecutive CI builds with no firmware change → the same build id → no update offered.
- [ ] 7. UDP from a different PC IP is ignored. **Not tested; deferred** (user, 2026-10-05): firmware behaviour from before this work.
- [x] 9. Unplug during a flash → Install again succeeds.
- [x] 10. Probe: a blank board, another Volco build, and no ESP32 on the port each give their message.
- [x] 11. Install over a configured board (NVS wiped) → "This controller isn't set up" (mirror kept) → Apply my saved setup.

## Before merging the branch

- [x] Go over the warnings in the latest CI runs of the branch (all jobs: compiler, analyzer, actions and tooling warnings) and address them. Asked by the user on 2026-10-05. Done 2026-10-05 (run 37379701225: 0 warnings; the only remaining line is the default-branch hint printed by actions/checkout).
- [ ] The firmware notices and the LGPL (spec §14, point 3): the user's decision before a release ships firmware.

## Already proven

- Stage 6: items 1, 5a, 9, 10 and 11 passed on Windows (user), CI builds 37296490575/37319755534/37335616849, 2026-10-05; 5b by two CI builds with no firmware change keeping build id 0af5cda4a575. Install time 28.9 s at 460800 baud. Item 5 passed with CI build 37361551674 (0af5cda4a575 → 0901f1a99d77; offered on the page, sidebar and tray; setup kept; Update took 29.2 s at 460800 baud).

- Stage 5: items 2, 3, 4, 6, 8, 12 and 13 passed on Windows (user), CI build 37262206851 from controller-config-stage2, 2026-10-05.

- Stage 3: items 3 and 12 on the stage 3 firmware (seeded config), 2026-10-04.
- Stage 4a: a config write over serial with `config_tool.py` (write → reboot → new CRC; INVALID, BAD_CRC, TOO_LARGE and timeout keep the old config), on the screen board, 2026-10-04.

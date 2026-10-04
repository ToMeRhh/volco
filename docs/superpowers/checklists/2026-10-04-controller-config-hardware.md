# Controller config: manual hardware checklist

From spec §12.7 (`docs/superpowers/specs/2026-10-03-controller-config-design.md`). Each item says which stage proves it. Record the date, the build, the board and the result next to each item.

**Setup:** Volco built by CI from the branch under test, on Windows, with the DIY reference board on USB. Firmware flashed with `pio run -e esp32 -t upload` until stage 6 brings Install.

## Stage 5 (Controller page)

- [ ] **2. Apply the DIY reference.** On the Controller page: Set up (or Set up from scratch) → Start from: DIY reference → Save to controller. Expect:
  - "Sending… → Restarting… → Saved. The controller restarted with the new setup."
  - Start test: all 5 sliders move their bars, all 3 buttons light their lamps.
  - The LED choices light the LEDs (pair: A and B).
  - Volume and mute don't change while testing.
  - Stop test.
- [ ] **3. Normal mode.** Volume and mute work, and the actions are unchanged.
- [ ] **4. Change a pin.** Move a slider or button to a free pin (rewire it), then save. The old pin no longer reacts and the new one does.
- [ ] **6. Wi-Fi.**
  - Advanced → Use Wi-Fi with a wrong password → save. It still works over USB.
  - Fix the password → save → set Volco's connection to Wi-Fi (UDP). It connects.
  - Change a behaviour setting and save over UDP. The save is confirmed after the reboot.
- [ ] **8. Unplug during a save.** Unplug right after Save. After reconnecting, the banner reports correctly whether the old or the new setup is on the controller.
- [ ] **12. Screens.** On the screen board: a layout with screens, made on the page (bus, address, SDA/SCL). The screens render. A behaviour change (brightness) takes effect after the save.
- [ ] **13. Reboot with the COM port open.** A save over USB: no "Connection lost", and the save is confirmed.

## Stage 6 (install and CI)

- [ ] 1. Install on a blank ESP32 → Hello, "Set up your controller".
- [ ] 5. Update firmware (a second build id) → the config survives (same CRC, no banner).
- [ ] 5a. Flash a `dev` build with `pio run -t upload` → the config survives; "Development firmware" with no update indicator; Install brings back the bundled build.
- [ ] 5b. Two consecutive CI builds with no firmware change → the same build id → no update offered.
- [ ] 7. UDP from a different PC IP is ignored.
- [ ] 9. Unplug during a flash → Install again succeeds.
- [ ] 10. Probe: a blank board, another Volco build, and no ESP32 on the port each give their message.
- [ ] 11. Install over a configured board (NVS wiped) → "This controller isn't set up" (mirror kept) → Apply my saved setup.

## Already proven

- Stage 3: items 3 and 12 on the stage 3 firmware (seeded config), 2026-10-04.
- Stage 4a: a config write over serial with `config_tool.py` (write → reboot → new CRC; INVALID, BAD_CRC, TOO_LARGE and timeout keep the old config), on the screen board, 2026-10-04.

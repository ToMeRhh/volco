# Design documents

The designs behind Volco's main subsystems, written before each was built. Code comments that say
"spec §N" point into the document for that area. Each one describes the design as approved; where
the code has moved on since, the code and `CLAUDE.md` are the current reference.

| Document | Covers |
|---|---|
| [`audio-core.md`](audio-core.md) | The Windows Core Audio engine: one audio thread, cached device and session registries, event-driven updates |
| [`volume-osd.md`](volume-osd.md) | The on-screen volume indicator |
| [`ux.md`](ux.md) | The settings window: pages, layout, colour and type tokens, accessibility |
| [`oled-screens.md`](oled-screens.md) | Per-button OLED screens, the Hello handshake and the Screen message |
| [`controller-config.md`](controller-config.md) | The controller config stored on the ESP32, its protocol, and installing firmware from the app |
| [`link-state.md`](link-state.md) | What "Connected" means, and the other link states |
| [`simulator.md`](simulator.md) | The web simulator: the 3D desk scene, the builder and pin allocation, demo mode, developer tools |
| [`custom-actions.md`](custom-actions.md) | Media, open-a-program and run-a-script button actions; the USB-only rule for programs and scripts |
| [`playing-indicator.md`](playing-indicator.md) | The animated equaliser on media buttons' screens; animated icons and partial screen pushes |

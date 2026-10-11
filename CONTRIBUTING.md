# Contributing to Volco

Thanks for your interest! Bug reports, ideas, hardware builds and pull requests are all welcome.

- **Found a bug or have an idea?** [Open an issue](https://github.com/ToMeRhh/volco/issues/new/choose). For bugs, the app's log (`%APPDATA%\Volco\volco.log`) and your controller layout help a lot.
- **Planning something big** (a macOS or Linux port, a new transport, a protocol change)? Open an issue first so we can agree on the approach before you put in the work.
- **Security issue?** Please don't open a public issue; see [`SECURITY.md`](SECURITY.md).

## Project layout

```
src/Volco.Core/        Platform-neutral core (net8.0): protocol, input, actions, config, runtime
src/Volco.App/         Avalonia UI (net8.0, platform-free): tray, settings pages, OSD, view models
src/Volco.Windows/     Windows host → Volco.exe: Core Audio engine, media sessions, firmware flasher
tests/                 xUnit tests for Core, Avalonia headless tests for App
firmware/              ESP32 firmware (PlatformIO); pure C++ libraries in firmware/lib are host-tested
proto/                 The controller config schema (protobuf), shared by the app and the firmware
tools/simulator/       Browser-based 3D controller simulator
docs/design/           Design documents; "spec §N" in code comments points into them
```

`Volco.Core` and `Volco.App` must stay platform-free: no P/Invoke and no Windows APIs. `tests/Volco.App.Tests/ArchitectureTests.cs` enforces it. Everything OS-specific comes in through interfaces (`IAudioEngine`, `IWindowEffects`, `IMediaSessions`, `IProgramLauncher`, `IFirmwareFlasher`) implemented in `Volco.Windows`.

## Building and testing

### App

You need the **.NET 10 SDK** (pinned in `global.json`; the projects target .NET 8). The tests run on any OS:

```bash
dotnet test tests/Volco.Core.Tests
dotnet test tests/Volco.App.Tests
```

`Volco.exe` has to be built on Windows:

```bash
dotnet publish src/Volco.Windows/Volco.Windows.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o ./publish/
```

A local build without the embedded firmware runs fine; it just can't install firmware. CI builds the firmware first and embeds it.

### Firmware

You need [PlatformIO Core](https://platformio.org/install/cli) 6.1.19 or later, and a host `g++` for the native tests.

```bash
cd firmware
pio run -e esp32                # build
pio run -e esp32 -t upload      # flash a development build (the controller's setup survives)
pio test -e native              # host-side tests of firmware/lib, no hardware needed
```

After changing `proto/volco_config.proto`, regenerate the shared test vectors with `python firmware/tools/gen_config_vectors.py`. After changing anything in `firmware/assets`, run `python3 firmware/tools/gen_assets.py` (needs Pillow 10 or later). [`firmware/README.md`](firmware/README.md) covers the code, the wire protocol and the rendering in detail.

### Simulator

No build step: open `tools/simulator/index.html` in a browser. Its tests run under Node:

```bash
cd tools/simulator && npm ci && node --test
```

## Pull requests

- Keep a PR focused on one change, and add or update tests with it. CI (`build.yml`) runs the C# tests, the firmware's native tests and ESP32 build, and the simulator's tests on every PR.
- Match the style of the code around your change.
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/) loosely: `feat(app): …`, `fix(firmware): …`, `docs: …`.
- The app and the firmware ship together, so a protocol or config change updates both sides in the same PR. There's no need for backward-compatibility shims.

By contributing, you agree that your contributions are licensed under the project's [GPL-3.0 license](LICENSE).

## Releases

Maintainers release by pushing a semver tag (`git tag v1.2.3 && git push origin v1.2.3`) or by running `release.yml` from the Actions tab. It builds a version-stamped `Volco.exe` with the matching firmware embedded, and attaches it to a GitHub Release with the firmware zip and its licence notices. The in-app updater installs the latest release.

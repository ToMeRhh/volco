# Security

## Reporting a vulnerability

Please report security issues privately through GitHub: **[Report a vulnerability](https://github.com/ToMeRhh/volco/security/advisories/new)** (the Security tab → *Report a vulnerability*). Don't open a public issue for them.

I'll reply as soon as I can, and credit you in the fix's release notes if you'd like.

## Supported versions

Only the [latest release](https://github.com/ToMeRhh/volco/releases/latest) gets fixes. The app updates itself, and it carries the matching controller firmware.

## Things to know

- **The network transports aren't authenticated.** Over Wi-Fi (UDP) or WebSocket, anything on your network that speaks the protocol can move volume and press buttons. Use them only on networks you trust. Because of this, **Open a program** and **Run a script** only run for presses that arrive over **USB**; over the network they're refused.
- **`Volco.exe` isn't code-signed yet.** Download it only from this repository's [Releases](https://github.com/ToMeRhh/volco/releases).

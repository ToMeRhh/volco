# Link state: what "Connected" means

Date: 2026-10-06. Status: implemented.

## Rule

**Connected** means Volco and the controller talk the same protocol: the controller was found
over a supported transport (USB serial, UDP, WebSocket) and sent a Hello with this app's
protocol version. An open COM port, a UDP packet or a WebSocket client is a transport fact, not
a connection. A board can be physically plugged in and still not be connected.

The UI never confuses the two layers:

| Word | Used for | Never for |
|---|---|---|
| Connected | `LinkState.Connected` (matching Hello) | an open port, a packet, a cable |
| Connecting… | an expected wait for the Hello | waiting for a port to appear |
| Disconnected | the user's Disconnect only | anything the user didn't do |
| plugged in / port / listening / answering | detail lines about the physical layer | headlines |

## States

`VolcoRuntime.LinkStatus` (`LinkState` + `LinkActivity`) is a pure function (`LinkStateLogic.Compute`)
of the user's Disconnect, `TransportStatus` (`Closed`/`Open`/`Failed`), `ControllerState`, the USB
silence flag, the held expected interruptions and a running config save. Priority:
Disconnected > Connected > Incompatible > Waiting (interruption, then save) > what the transport says.

| State | Headline | Dot | Tray |
|---|---|---|---|
| Connected | Connected | green | normal (attention icon when the controller needs the user) |
| Waiting | Connecting… / Checking firmware… / Installing firmware… / Updating firmware… / Controller restarting… | grey | normal |
| Searching | Looking for your controller | grey | red |
| PortBusy | Can't use COM3 / Can't use port N / No port chosen | red | red |
| NoAnswer | Controller not responding (USB only, after 5 s of silence) | red | red |
| Incompatible | Firmware update needed | amber | red |
| Disconnected | Disconnected | grey | red |

The tray is red whenever `LinkStatus.IsDown` (anything but Connected and Waiting); the
Controller-page attention line and menu item show whether or not the icon is red. The detail line
gives the hardware summary when connected on every transport ("5 sliders · 3 buttons …"), and
otherwise what the state means and what to do (`LinkText.Explanation`).

## Expected interruptions

Gaps Volco causes itself are never failures. `VolcoRuntime.ExpectInterruption(activity, limit?)`
returns a handle; while any is held the link reports `Waiting` with the newest firmware activity
(a firmware activity outranks a connect grace), and the silence timer is held back (its end
re-checks). A controller that answers meanwhile is still Connected or Incompatible.

- Firmware probe, install, update: `FirmwareService` holds one for the whole operation,
  including the post-resume wait for the Hello (replaces the old `HoldSilence`).
- Starting a transport (launch, Connect, Reconnect, a connection-config change, the resume after a
  firmware operation): a connect grace of `VolcoRuntimeOptions.ConnectGrace` (3 s), taken before
  the old transport is dropped so no red flash shows between the two.
- A config save: `ControllerConfigService.Operation == Writing` reads as "Controller restarting…"
  while the transport drops for the reboot (UDP).

When the window ends without an answer, the real state shows (e.g. Controller not responding).
An unplug or crash Volco didn't cause turns red at once.

## Transport

`TransportStatus` replaces `ConnectionStatus` (`Disconnected/Connected/Error`) so "connected" no
longer exists at the transport layer. USB: access denied while opening means another app holds the
port (`Failed`, still retrying); a missing port is `Closed`. `IControllerConnection.PeerAddress`
gives UDP's sender IP for the detail line.

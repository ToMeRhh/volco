# Volco WebSocket Simulator

A minimalistic web-based simulator to test the Volco application without physical hardware.

## How to use

1.  **Start Volco** on your Windows machine.
2.  Go to the **Connection** tab in Volco settings.
3.  Select **WebSocket** as the connection type.
4.  Ensure the port matches (default: `8765`).
5.  Open `index.html` in your web browser.
6.  Click **Connect** in the simulator.
7.  Move the sliders and click the buttons to see Volco react!

## Features

- 5 vertical sliders (12-bit ADC range, 0–4095)
- 3 buttons: 2 mute buttons with LED indicators, 1 device selector with 2 LED indicators
- LED state feedback — mute LEDs and device selector LEDs update in response to server messages
- Auto-reconnect option with 3-second interval

## Protocol Implementation

The simulator implements the full bidirectional Volco binary protocol:
- **Framing**: `0xAA` [Length] [Payload] [XOR Checksum]
- **Slider** (outbound): `0x01` [Index] [Value High] [Value Low] — 12-bit (0–4095)
- **Button** (outbound): `0x02` [Index] [State (0 or 1)]
- **Mute State** (inbound): `0x03` [Button Index] [Muted (0 or 1)]
- **Active Device** (inbound): `0x04` [Device Index]

## Troubleshooting

- Ensure Volco is running and WebSocket is selected.
- If you change the port in Volco, you must also change it in the simulator.
- Check the browser console (F12) for connection errors.

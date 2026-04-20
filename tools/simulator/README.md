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

## Protocol Implementation

The simulator implements the Volco binary protocol:
- **Framing**: `0xAA` [Length] [Payload] [XOR Checksum]
- **Slider Payload**: `0x01` [Index] [Value High Byte] [Value Low Byte]
- **Button Payload**: `0x02` [Index] [State (0 or 1)]

## Troubleshooting

- Ensure Volco is running and WebSocket is selected.
- If you change the port in Volco, you must also change it in the simulator.
- Check the browser console (F12) for connection errors.

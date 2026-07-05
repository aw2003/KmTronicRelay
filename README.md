# KmTronicRelay

A desktop GUI for **KMTronic 8-port USB relay boards** (FTDI serial). Control relay outputs from your PC without a web interface or browser — useful for bench work such as remote jumper routing, power-path switching, and toggling hardware straps.

Built with [.NET 8](https://dotnet.microsoft.com/) and [Avalonia UI](https://avaloniaui.net/). Publishes as a **single self-contained Windows executable** — no .NET runtime required on the machine you run it on.

**License:** [MIT](LICENSE) — see [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES) for bundled libraries and icon attributions.

---

## What it is for

KMTronic relay boards expose eight independent relay contacts over USB. This app lets you:

- Turn individual relays **on** or **off** from a simple window
- Label each relay (e.g. *SBC Dediprog route*, *Relay 03*)
- Assign an optional COM port per relay row and open a **UART monitor** when that relay is on
- Recover automatically after USB unplug/replug, including when Windows assigns a **different COM port**

Typical use cases:

- Switching boot/programming jumpers on an SBC from the desk
- Remote control of relay-strapped test fixtures
- Quick manual ON/OFF of equipment lines without a separate controller

**Supported hardware:** KMTronic USB relay boards using the FTDI USB-serial chip (**VID `0403` / PID `6001`**), 9600 baud.

**Supported OS:** Windows 10/11 (64-bit).

---

## Features

### Relay control

- **8 relay channels** with editable names (max 20 characters; empty names reset to *Relay 01* … *Relay 08*)
- **Click** the status dot to toggle a relay (hover for hints)
- **Click and drag** across multiple dots to toggle a range
- **All On / All Off** buttons
- **Double-click** an **ON** relay to **power-cycle** it: OFF → 2 second wait → ON, with a centered blue countdown ring and *Cycle* status text

### Connection and recovery

- **Automatic board detection** — probes FTDI COM ports and validates the KMTronic response
- **Remembers the working port** (e.g. `COM7`) in settings; tries it first on next launch
- **Rescan** if the saved port fails (handles COM port changes after reconnect)
- **Reconnect monitoring** — detects USB disconnect and attempts to reconnect
- **State restore** — after reconnect, reapplies relay ON/OFF states from before the disconnect
- **Status bar** — shows connection state for the KmTronic 8-Port USB Relay

### Per-relay COM port and UART monitor

Each relay row has a **COM dropdown** plus action buttons:

| Control | Purpose |
|---------|---------|
| COM dropdown | Assign a serial port for UART monitoring (`N/A` = none) |
| Eraser | Clear COM selection back to `N/A` |
| Gear | Edit baud rate, data bits, stop bits, parity, flow control, **timestamps**, and **file logging** |

Additional behaviour:

- **Scroll wheel** over the COM dropdown moves the selection up/down
- The relay board’s own COM port is excluded from the list
- Each port can only be assigned to **one** relay row
- When a relay turns **on** and has a COM assigned, a **monitor window** opens automatically
- Monitor closes when the relay turns **off**, you close the window, or **All Off** is pressed (stays open during power-cycle)
- Monitor supports **VT100** text view and **Hex** dump toggle, plus a send line (Enter or Send)
- **Timestamps** — optional `[yyyy-MM-dd HH:mm:ss.fff]` prefix on received UART lines in **VT100** view and in the log file when enabled (hex dump shows offsets only)
- **Log to file** — optional append-only `.log` capture of received UART data; choose path via **Browse…** in the gear dialog
- Changing serial settings in the gear dialog updates an **open** monitor immediately

### Other

- **About** dialog (? button) — version and license info
- System **light / dark theme** (follows OS)
- **Self-contained** publish — copy one file, run it
- Settings stored in the user profile (see below)

---

## Requirements

### To build (developer machine)

- **WSL Ubuntu** (or Linux) with **.NET SDK 8.0** — used to cross-publish the Windows `.exe`

```bash
sudo apt update
sudo apt install -y dotnet-sdk-8.0
```

### To run the published app

- **Windows 10 or 11** (64-bit)
- FTDI USB serial driver for the KMTronic board

---

## Building

From the repository root in WSL (or Linux):

```bash
chmod +x build.sh   # first time only
./build.sh
```

The script will:

1. Restore NuGet packages  
2. Fail if known vulnerable dependencies are detected  
3. Publish a **Release** self-contained single-file binary for **Windows x64**  
4. Run **unit tests** (`tests/KmTronicRelay.Tests`)

### Output

| Target | Path |
|--------|------|
| Windows | `build/KmTronicRelay.exe` |

Intermediate build files (`build/bin/`, `build/obj/`) are kept under `build/` and are not required in source control.

Copy `build/KmTronicRelay.exe` to a Windows PC and run it. No .NET installation is needed there.

On Windows, you can also trigger the build from a Windows shell via WSL:

```cmd
wsl ./build.sh
```

(`build.cmd` in the repo only prints this hint.)

---

## Running

Copy `KmTronicRelay.exe` anywhere on Windows and double-click it (or run from a terminal).

On first launch, if no relay board is found, an error dialog is shown. Connect the KMTronic board via USB and restart the app, or wait for the background reconnect timer.

Windows SmartScreen may warn about an unsigned publisher on first run — this is normal for unsigned executables.

---

## Usage

| Action | How |
|--------|-----|
| Toggle one relay | Click the red/green status dot |
| Toggle several relays | Click and drag across dots |
| Power-cycle (relay must be ON) | Double-click the status dot; wait for the blue ring |
| All relays on/off | **All On** / **All Off** buttons |
| Rename a relay | Edit the name field (max 20 chars; clear and tab out to restore default) |
| Assign a COM port | Choose from the dropdown, or scroll wheel while hovering |
| Clear COM assignment | Eraser button (when a COM is selected) |
| UART settings | Gear button (when a COM is selected) — baud, parity, timestamps, log file |
| About / version | **?** button (top right) |

**Hover tooltips** on status dots, COM dropdowns, name fields, and buttons explain each control.

**Status dot colours:** red = OFF, green = ON.  
**Status text:** *Off*, *On*, or *Cycle* during power-cycle.

Settings (relay names, board port, per-row COM ports, UART parameters) are saved when you change them and again when you **close the window**.

---

## Settings file

| OS | Location |
|----|----------|
| Windows | `%LocalAppData%\KmTronicRelay\settings.json` |

Example structure:

```json
{
  "RelayNames": ["Relay 01", "Relay 02", "Relay 03", "Relay 04", "Relay 05", "Relay 06", "Relay 07", "Relay 08"],
  "ComPort": "COM7",
  "ChannelComPorts": ["N/A", "COM3", "N/A", "N/A", "N/A", "N/A", "N/A", "N/A"],
  "ChannelSerialSettings": [
    {
      "BaudRate": 115200,
      "DataBits": 8,
      "StopBits": "One",
      "Parity": "None",
      "Handshake": "None",
      "LogToFile": false,
      "LogFilePath": "",
      "ShowTimestamps": false
    }
  ]
}
```

- **`ComPort`** — last known good port for the relay **board** itself  
- **`ChannelComPorts`** — optional serial port per relay row (`N/A` = none)  
- **`ChannelSerialSettings`** — UART parameters per row (defaults: 115200 8N1, no flow control; optional timestamps and `.log` file capture)

---

## Development

| Task | Command |
|------|---------|
| Full build (test + publish) | `./build.sh` |
| Unit tests only | `dotnet test tests/KmTronicRelay.Tests/KmTronicRelay.Tests.csproj -c Release` |
| Open solution | `KmTronicRelay.sln` |

Continuous integration runs on push/PR to `main` via GitHub Actions (build + test on Ubuntu). See [CONTRIBUTING.md](CONTRIBUTING.md) for code-style and PR guidelines.

---

## Project layout

```text
KmTronicRelay/
├── KmTronicCommands.cs      # Serial protocol, FTDI detection, settings, reconnect logic
├── MainWindow.axaml         # Window layout
├── MainWindow.axaml.cs      # UI logic (clicks, COM dropdowns, power-cycle, UART monitor)
├── KmTronicRelay.csproj     # Project and dependencies
├── KmTronicRelay.sln        # Solution (app + tests)
├── LICENSE                  # MIT License (Adam Tsahor)
├── THIRD-PARTY-NOTICES      # Avalonia, .NET, Material Design icons, etc.
├── CONTRIBUTING.md          # Build, test, and PR guidelines
├── ico1.ico                 # Application icon
├── build.sh                 # WSL build script (tests + Windows x64 publish)
├── build.cmd                # Windows wrapper that runs build.sh via WSL
├── Directory.Build.props    # Routes bin/obj under build/ (per project)
├── .github/workflows/ci.yml # GitHub Actions CI
├── tests/KmTronicRelay.Tests/  # xUnit tests
├── include/                 # Avalonia bootstrap (entry point, app manifest)
│   ├── Program.cs
│   ├── App.axaml
│   ├── App.axaml.cs
│   └── app.manifest
└── README.md
```

Build artifacts (`build/`) are generated locally and are not required in source control.

---

## Troubleshooting

| Problem | Things to check |
|---------|-----------------|
| *KmTronic 8-Port USB Relay not found* | Board plugged in; FTDI driver installed; correct VID/PID; no other app holding the port |
| Wrong relay state after reconnect | Expected — app restores last UI states; verify USB cable and port in status bar |
| COM dropdown greyed out | No extra serial ports available, or all are already assigned / excluded |
| COM monitor fails to open | Port in use by another program; check baud/settings via gear button |
| SmartScreen warning | Unsigned exe — click *More info* → *Run anyway*, or code-sign the binary |

---

## License

MIT License — see [LICENSE](LICENSE). Copyright (c) 2026 Adam Tsahor.

Third-party components are listed in [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES).

---

## Acknowledgements

- [KMTronic](https://www.kmtronic.com/) relay hardware  
- [Avalonia UI](https://avaloniaui.net/) desktop framework  
- [Material Design Icons](https://github.com/google/material-design-icons) (Apache 2.0) — help and gear icons

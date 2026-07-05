# Contributing to KmTronicRelay

Thank you for your interest in improving KmTronicRelay. This document summarizes how to build, test, and submit changes.

## Prerequisites

- **WSL Ubuntu** (or Linux) with **.NET SDK 8.0**
- For running the published app: **Windows 10/11** (64-bit) and the FTDI USB driver

Install the SDK on Ubuntu:

```bash
sudo apt update
sudo apt install -y dotnet-sdk-8.0
```

## Build and test

From the repository root:

```bash
chmod +x build.sh   # first time only
./build.sh
```

`build.sh` restores packages, checks for known vulnerable dependencies, runs unit tests, and publishes a self-contained Windows executable to `build/KmTronicRelay.exe`.

Run tests only:

```bash
dotnet test tests/KmTronicRelay.Tests/KmTronicRelay.Tests.csproj -c Release
```

If you see duplicate assembly attribute errors (`CS0579`) after mixing `dotnet publish` and `dotnet test`, clear intermediates first:

```bash
rm -rf build/obj build/bin
```

On Windows, you can invoke the same script through WSL:

```cmd
build.cmd
```

## Project layout

| Path | Purpose |
|------|---------|
| `KmTronicCommands.cs` | Relay driver, settings, reconnect logic |
| `MainWindow.axaml(.cs)` | UI, COM settings, UART monitor, power-cycle ring |
| `include/` | Avalonia bootstrap (`Program.cs`, `App.axaml`) |
| `tests/KmTronicRelay.Tests/` | xUnit tests for portable helpers |
| `build.sh` | Restore, test, publish pipeline |

Build artifacts live under `build/` and are not committed.

## Code style

- Target **.NET 8** with nullable reference types enabled.
- Keep relay-board logic in `KmTronicCommands.cs`; UI logic stays in `MainWindow.axaml.cs` (no new logic `.cs` files unless discussed first).
- Document **public and internal** methods with Doxygen-style `/** @brief ... */` blocks (existing convention).
- Prefer small, focused diffs that match surrounding naming and patterns.
- Windows-specific APIs are expected (`System.IO.Ports`, WMI); guard or document when adding platform-specific code.

## Settings and persistence

User settings are stored at `%LocalAppData%\KmTronicRelay\settings.json`. When adding fields:

1. Extend `RelaySettings` / `SerialPortSettings` in `KmTronicCommands.cs`.
2. Normalize values in `NormalizeSerialSettings` (or equivalent).
3. Update `Clone()` and README examples.
4. Add unit tests for normalization when logic is non-trivial.

## Pull requests

1. Fork and create a feature branch from `main`.
2. Run `./build.sh` (or at least `dotnet test` + `dotnet publish`) before opening a PR.
3. Describe user-visible changes and any hardware tested.
4. CI runs on GitHub Actions (`.github/workflows/ci.yml`) — ensure it passes.

## Reporting issues

Include OS version, KMTronic board model, COM port behaviour, and steps to reproduce. Redact serial numbers or paths if sharing logs.

# KmTronicRelay

A desktop GUI for **KMTronic 8-port USB relay boards** (FTDI serial), created with the help of an AI coding assistant.

Built with [.NET 8](https://dotnet.microsoft.com/) and [Avalonia UI](https://avaloniaui.net/). Publishes as a **single self-contained Windows executable** — no .NET runtime required on the machine you run it on.

**License:** [MIT](LICENSE) — see [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES) for bundled libraries and icon attributions.

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
- FTDI USB serial driver for the KMTronic detection
  
---

## License

MIT License — see [LICENSE](LICENSE). Copyright (c) 2026 Adam Tsahor.

Third-party components are listed in [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES).

---

## Acknowledgements

- [KMTronic](https://www.kmtronic.com/) relay hardware  
- [Avalonia UI](https://avaloniaui.net/) desktop framework  
- [Material Design Icons](https://github.com/google/material-design-icons) icons

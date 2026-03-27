# MX907600A Window Fix

Portable launcher and draw proxy for the legacy `MX907600A` OTDR application.

This project fixes the main usability problems of the original software on modern displays:

- enables maximization of the main window,
- scales the reflectogram area to the resized graph control,
- keeps legacy floating UI panels in usable positions,
- preserves the original application files by wrapping the draw DLL instead of patching the EXE directly.

## What is in this repository

- `src/launcher/Program.cs`
  Portable elevated launcher that embeds the required DLL payloads and starts `MX907600A.exe`.
- `src/drawproxy/Draw9076Proxy.c`
  Proxy `Draw9076.dll` that forwards to the vendor DLL and adjusts graph sizing / selected UI window moves.
- `src/drawproxy/Draw9076Proxy.def`
  Export definition matching the original DLL ABI.
- `scripts/build.ps1`
  Builds the proxy DLL and single-file launcher.
- `scripts/install.ps1`
  Copies the built launcher next to a local `MX907600A.exe`.

## Legal note

Do not publish vendor binaries from the original application.

This repository intentionally contains only original source code for the patch layer. The generated launcher embeds only the custom proxy DLL. On first run it backs up the locally installed vendor `Draw9076.dll` to `Draw9076.real.dll` and then drops the proxy in its place.

## Requirements

- Windows
- Visual Studio Build Tools or Visual Studio with 32-bit MSVC tools
- .NET Framework C# compiler at `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
- A local installation of `MX907600A`

## Build

Example:

```powershell
pwsh ./scripts/build.ps1 -AppDir "C:\Program Files (x86)\MX907600A"
```

Output:

- `dist/MX907600A_FixedLauncher.exe`

## Install

Example:

```powershell
pwsh ./scripts/install.ps1 -AppDir "C:\Program Files (x86)\MX907600A"
```

This copies `dist/MX907600A_FixedLauncher.exe` into the application folder.

## Usage

Put `MX907600A_FixedLauncher.exe` in the same folder as `MX907600A.exe` and run it.

The launcher:

- auto-elevates through UAC when needed,
- backs up the local vendor `Draw9076.dll` to `Draw9076.real.dll` when needed,
- writes the embedded proxy DLL into the application directory,
- starts the original `MX907600A.exe`,
- applies the window/layout fixes.

## Repository policy

Safe to publish:

- source code,
- scripts,
- documentation,
- screenshots that do not contain sensitive customer data.

Do not publish:

- original vendor EXE/DLL files,
- customer OTDR traces,
- measurement files such as `.SOR`,
- binaries built from vendor files if you do not have redistribution rights.

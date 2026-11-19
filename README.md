# Single-executable YukMain CLI host (new)

**Recommended host launcher:** `YukMain.Host.exe` (Windows 10/11 x64).
The executable combines the existing HTTPS/WSS Bridge and HIDMaestro controller engine without combining their privileges. No WPF GUI is started. The two processes are instances of the **same executable**, not two downloadable executables.

1. Open a normal terminal and run `YukMain.Host.exe` (do NOT right-click "Run as administrator").
2. Approve the Windows UAC prompt for the **separate** controller engine.
3. After the driver initializes, enter the YukMain HTTPS URL, room code and temporary pairing code from Host Studio.
4. Remote browser players automatically drive P2/P3/P4 through the normal-user WebSocket Bridge and the existing same-user, medium-integrity named pipe.
5. Stop the CLI to release controller inputs; its elevated helper also watches the parent process, including after unexpected exit.

**Distribution:** CI publishes one self-contained single-file EXE and GitHub Releases attach that EXE directly. Embedded HIDMaestro license notices can be read with `YukMain.Host.exe --licenses`. Native bundled resources may extract to a .NET runtime temporary directory; the HIDMaestro driver still installs to Windows with administrator consent.

**Constraints:** The elevated engine and CLI must belong to the same Windows account (UAC "consent" for an administrator account). A standard user entering *another* administrator account's credentials may fail the existing named-pipe ownership check. Stop the old GUI Agent before starting the CLI; both use the same pipe name. Only the elevated engine initializes virtual controllers; the network Bridge never runs as administrator. There is no automatic server-pairing UI yet: the room and one-time code still come from Host Studio.

## Existing local test GUI

The old WPF GUI and stand-alone Bridge remain in source for debugging and regression builds, but are no longer the recommended packaged host entry point. The remaining documentation below describes the old workflow.

---

# YukMain Host Agent

A standalone Windows host application for virtual Xbox 360 controllers, built for YukMain. **This branch is a local-test MVP; it does not connect to WebSocket yet.**

## Scope

- Windows 10/11 x64, C#/.NET 10, WPF.
- HIDMaestro v2.2.0 pinned using the official x64 release and verified SHA-256.
- Three independent virtual-controller slots (P2, P3, P4).
- Local buttons (A/B/X/Y, bumpers, Start/Back), D-pad, dual sticks, triggers.
- Five-second delayed A-button press for in-game testing.
- Neutralize inputs on reset and controller removal.
- Build and package on GitHub Actions, without driver installation on CI.

## Setup

Run from PowerShell on a Windows development machine with the .NET 10 SDK:

```powershell
./scripts/prepare-hidmaestro.ps1
dotnet restore YukMain.Host.slnx
dotnet build YukMain.Host.slnx -c Release
dotnet run --project tests/YukMain.Host.Tests/YukMain.Host.Tests.csproj -c Release
dotnet run --project src/YukMain.Host.Desktop/YukMain.Host.Desktop.csproj -c Release
```

Windows will ask for administrator approval because creating virtual controllers and installing the HIDMaestro user-mode driver require elevation.

For a build that does not require the .NET runtime installed:

```powershell
dotnet publish src/YukMain.Host.Desktop/YukMain.Host.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

To obtain a packaged EXE via GitHub, open **Actions → Windows Build → latest run → Artifacts**. A tagged `v*` push also attempts to create a GitHub Release.

## Local hardware test

1. Run YukMain Host Agent as administrator.
2. Click **Initialize Driver**. First run installs a locally trusted certificate and the HIDMaestro driver. This changes Windows machine configuration.
3. Click **Create Controller** under Player 2.
4. Open **Windows Controller Tester** (or Win+R → `joy.cpl`).
5. Find the virtual Xbox controller, select Properties, then toggle A/B/X/Y or move analog sliders.
6. Launch Overcooked! 2 (or another locally installed game), use **Test A in 5s**, and switch focus to the game.
7. Repeat with Players 3 and 4. Verify individual inputs and remove each controller before exit.

**Steam game compatibility and Windows gamepad ordering must be tested on physical Windows hardware**. The logical P2/P3/P4 IDs are not guaranteed to equal in-game player indices.

## Security & limitations

- This MVP requests administrator privileges for the whole application because HIDMaestro requires them for both initial installation and controller creation. **Do not add WebSocket server/client or untrusted input directly to this elevated process.** A subsequent milestone will move the SDK into a small elevated local helper and keep the GUI/network client at standard user privilege.
- The driver and its local signing certificate are system-wide changes. Only initialize after reviewing the upstream SDK and trusting its distribution.
- Build CI runs without creating controllers. It cannot prove Windows driver installation or game behavior.
- GitHub release EXE is unsigned by YukMain; Windows SmartScreen may display a warning. A trustworthy public release needs a signing/distribution plan.
- The WPF app is a manually tested prototype, not a security-hardened public installer.

## Structure

```
src/YukMain.Host.Core/       # backend-independent controller API and state
src/YukMain.Host.HidMaestro/ # only project that references HIDMaestro
src/YukMain.Host.Desktop/    # WPF desktop local testing application
tests/YukMain.Host.Tests/    # hardware-free controller manager tests
scripts/prepare-hidmaestro.ps1
.github/workflows/ci.yml
```

## Upstream license

HIDMaestro: https://github.com/hifihedgehog/HIDMaestro, MIT License.
The upstream license notice is retained in `docs/HIDMaestro-LICENSE.txt` and in distribution archives.

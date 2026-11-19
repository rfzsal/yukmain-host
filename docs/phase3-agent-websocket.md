# Phase 3 — YukMain Host Agent WebSocket integration

This project now contains **two executables**:

- `YukMain.Host.Agent.exe`: elevated WPF controller engine/HIDMaestro GUI. It has **no HTTP or WebSocket client**.
- `YukMain.Host.Bridge.exe`: ordinary-user console process that pairs with the Go room API, receives controller snapshots over WSS, and forwards them over a current-user-only Windows named pipe.

The pipe `yukmain-controller-input-v1` carries controller snapshots, and the elevated UI rejects other message types and slots. An explicit Windows ACL grants access only to the engine's Windows account; a medium-integrity label permits that account's non-elevated Bridge to write. The Bridge also verifies the pipe owner's account. The elevated process retains HIDMaestro and the locally installed driver. The unprivileged Bridge never installs drivers or handles elevated device APIs.

## Build

On Windows with .NET 10 SDK installed:

```powershell
git checkout feat/agent-websocket
powershell -ExecutionPolicy Bypass -File .\scripts\prepare-hidmaestro.ps1
dotnet restore YukMain.Host.slnx
dotnet build YukMain.Host.slnx -c Release
dotnet run --project tests/YukMain.Host.Tests -c Release
dotnet publish src/YukMain.Host.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/agent
dotnet publish src/YukMain.Host.Bridge -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/bridge
```

## Live end-to-end test

1. Deploy the matching Phase 3 YukMain Go/React feature branch.
2. Open YukMain Agent elevated. Click **Initialize Driver**; optionally create virtual P2/P3/P4. Keep it running.
3. Open YukMain on the website, create a room; in **Host Studio → WebSocket Relay** click **Generate pairing code**.
4. Launch **YukMain.Host.Bridge.exe from a normal (not Administrator) PowerShell**. Enter the HTTPS website URL, room number, and 8-character code shown in the browser.
5. The Bridge prints *Local controller engine connected*, *Paired*, and *WSS connected*. The host studio shows *Agent connected*. The browser host receiver disconnects to free the one available host socket.
6. Open the room as a guest from another browser. Request controller access, and approve P2 in the host browser.
7. Guest clicks **Press A**, then **Release all**. In `joy.cpl`/gamepad tester, P2 virtual controller should move.
8. Disconnect the guest, revoke their slot, and stop the Bridge. Inputs must neutralize. Verify P3/P4 similarly.

Controller devices are created on the first authorized frame after the driver is initialized. The input watchdog resets stale inputs after 1.2 seconds (future continuous gamepad polling is intended for holding buttons longer).

The Bridge holds the agent token in memory only and never logs it. The one-time pairing code expires in 3 minutes. Re-pairing invalidates the prior bridge token. If you see HTTP 409 on WSS, the old host debug receiver may still be connected; wait for the browser to yield its connection and generate another pairing code.

## Safety notes

This is a developer MVP, **not a hardened installer**. The pipe ACL restricts access to processes belonging to the same Windows account, rather than all local users. `PipeOptions.CurrentUserOnly` is intentionally not used because it also requires matching elevation levels on Windows. Account checks do not distinguish trusted programs running as that user; future production work should add explicit broker authentication, consider interactive UI consent, and sign the distribution. Do not run the Bridge elevated. The elevated WPF controller component does not parse network packets directly.

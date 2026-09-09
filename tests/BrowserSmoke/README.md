# Browser integration smoke tests

Windows 11, .NET 8 SDK, Edge, Node.js and a working licensed Pianoteq are required. Close the Nocturne desktop app first. These tests stop current MIDI playback and play real test notes; `--isolation` also plays a two-second tone in a separate process from Pianoteq. Set `PIANOTEQ_PATH` if needed.

Run from this folder:

```powershell
dotnet run -c Release -- --capture
dotnet run -c Release -- --isolation
npm install
```

Start the local test host in terminal 1:

```powershell
Remove-Item stop,disable,enable -ErrorAction SilentlyContinue
dotnet run -c Release
```

When it prints `HOST READY`, run `npm test` in terminal 2 (same working directory). This checks decoded WebRTC and PCM audio, real touch events at a mobile viewport, shared-note reference counts, recording input lock, watchdog and abrupt disconnect release, authentication failure, missing file endpoints and JavaScript/CSP errors. It saves desktop/mobile screenshots. `Set-Content stop stop` shuts down the test host; it also stops after ten minutes.

The host inspects private note state under the service lock, only for assertions. No diagnostic endpoint is added to the actual server. Invitation keys, test state, screenshots and build outputs are ignored by Git. A mobile viewport is not a substitute for testing physical iOS/Android devices or public TURN connections.

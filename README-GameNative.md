# Cypress GameNative / Wine compatibility build

This is the complete Cypress source tree with the GameNative/Wine browser fallback changes included.

## Build on Windows

Requirements: .NET 8 SDK, PowerShell 7 (`pwsh`), and an internet connection for the pinned SimpleWebView2Browser release download.

From this directory run:

```powershell
dotnet publish .\Launcher\CypressLauncher.csproj -c Release -f net8.0-windows -r win-x64 --self-contained false -o .\Launcher\publish\GameNative /p:LangVersion=latest
```

The build target downloads SimpleWebView2Browser v0.1, verifies the release archive SHA-256, and places the browser files in the publish output under `Browser\`. Start `CypressLauncher.exe` from that publish folder.

When launched under Wine/Proton (including GameNative), Cypress selects the local-browser compatibility mode automatically. It starts a loopback-only server for the Cypress UI, launches the bundled SimpleWebView2Browser with that URL, and bridges UI messages to the existing launcher handler. No GameNative APK changes or per-user browser-path configuration are required.

The browser host requires a working WebView2 Runtime in the Wine prefix. The compatibility code applies the Wine `msedgewebview2.exe` Windows-version override (`win8`) reported as necessary for WebView2 on affected Wine builds.

## Notes

- This is a source archive, not a compiled release. Build the Windows launcher using the command above.
- Git metadata and generated build caches are intentionally omitted; the source files, assets, server files, documentation, and required LFS-materialised game patch binaries are included.
- Runtime behaviour still needs confirmation in the target GameNative/Wine version. Diagnostics are written to `%LOCALAPPDATA%\Cypress\browser-fallback.log` inside the Wine prefix.

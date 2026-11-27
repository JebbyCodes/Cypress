# Patch for `Launcher/CypressLauncher/Program.cs`

One edit. No csproj change is required.

## 0. Remove the previous file

Delete `Launcher/CypressLauncher/AvaloniaFallbackHost.cs` if it is present.
It referenced the Avalonia packages, which are not in `CypressLauncher.csproj`,
and that is what broke CI (CS0246 on `Avalonia`, `AppBuilder`, `Window`, etc.).
`NativeFallbackHost.cs` needs no new packages: the project already sets
`<UseWindowsForms>true</UseWindowsForms>`.

## 1. Replace the body of `RunBrowserFallback`

Find:

```csharp
private static void RunBrowserFallback(MessageHandler handler)
{
    string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    ...
}
```

Replace the whole method with:

```
private static void RunBrowserFallback(MessageHandler handler)
{
    using var host = new NativeFallbackHost(handler);
    handler.SetBrowserMessageSender(host.SendToBrowser);
    handler.SetBrowserCloseAction(host.RequestShutdown);
    handler.SetBrowserExternalUrlOpener(host.OpenExternalUrl);
    Console.Error.WriteLine("Cypress is using its GameNative/Wine native GDI rendering mode.");
    host.Start(); // blocks until the window is closed
    handler.KillAllInstances();
}
```

## 2. Nothing else changes

`ShouldUseBrowserFallback()`, `IsGameNativeSession()`,
`ConfigureWineWebView2Compatibility()` and all `#if WINDOWS` blocks stay exactly
as they are. `BrowserFallbackHost.cs` can stay in the tree as a manual WebView2
escape hatch; it is simply no longer called on the GameNative path.

</BDS:create_file>

<BDS:create_file fileName="Launcher/README-GameNative-Native.md">

```markdown
# Cypress - GameNative native rendering host (overlay patch)

## What this archive is

An **overlay patch**, not a full source tree. Copy over an existing
`JebbyCodes/Cypress` checkout:

Launcher/CypressLauncher/NativeFallbackHost.cs   (new file)
Launcher/PATCH_Program.cs.diff.md                (instructions)
Launcher/README-GameNative-Native.md             (this file)

```

Then follow `PATCH_Program.cs.diff.md`: delete `AvaloniaFallbackHost.cs` if
present, and replace the body of `RunBrowserFallback`. That is the entire change.
No csproj edit, no new NuGet packages.

## Fixing the CI failure

The earlier patch added `AvaloniaFallbackHost.cs` but the Avalonia packages were
never added to `CypressLauncher.csproj`, so CI failed with CS0246 on every
Avalonia type. `NativeFallbackHost.cs` uses `System.Windows.Forms`, which the
project already enables (`<UseWindowsForms>true</UseWindowsForms>`, used by the
tray icon and `MessageBox` in `Program.cs`). It compiles with the existing
package set.

## Why WinForms and not Avalonia

Both avoid Chromium. WinForms wins here because it needs no new dependency and
cannot regress the build. WinForms renders through GDI/User32 and never touches
DirectComposition or D3D, which is exactly the subsystem that fails inside
WebView2/Chromium under Wine.

## Status (honest)

- **Not compiled by me.** No .NET SDK, Wine or GameNative in this environment.
  Do not treat it as verified. It does remove the CS0246 cause, since it
  references only `System.Windows.Forms`/`System.Drawing`, already in the project.
- Correct against the reviewed source (`Program.cs`, `BrowserFallbackHost.cs`,
  `core.js`): diagnosis and bridge wiring
  (`SetBrowserMessageSender` / `HandleMessage(null, json)` / external opener).
- **Reduced-scope UI.** The window exercises the real backend (auth, instances,
  browser list) through buttons and a raw-JSON box. It does not reproduce every
  tab, the playlist builder, pickers, translations or docs pages. Full parity
  requires porting `wwwroot/js/*.js` and `wwwroot/css/*.css`.
- **EA OAuth not verified.** `EaLoginWindow.cs` was not reviewed. On GameNative
  `OpenExternalUrl` routes to the Android browser; confirm the redirect returns
  into the app on real hardware.
- Only `checkAuth` is confirmed from `core.js`; the other button action strings
  are illustrative and should be corrected against `MessageHandler.*.cs`.

## Remaining work

1. Port the full HTML UI for complete feature parity.
2. Confirm every backend action string.
3. Verify EA OAuth round-trip on GameNative hardware.
4. Windows 11 regression run.

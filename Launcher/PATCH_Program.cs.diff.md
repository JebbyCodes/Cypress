# Patch for `Launcher/CypressLauncher/Program.cs`

Two edits. Nothing else in `Program.cs` changes.

## 1. Add Avalonia packages to `Launcher/CypressLauncher.csproj`

The file is at `Launcher/CypressLauncher.csproj` (NOT inside `CypressLauncher/`).
Add inside the existing `<ItemGroup>` that already holds `Photino.NET`:

```xml
<PackageReference Include="Avalonia" Version="11.1.*" />
<PackageReference Include="Avalonia.Desktop" Version="11.1.*" />
<PackageReference Include="Avalonia.Themes.Fluent" Version="11.1.*" />
<PackageReference Include="Avalonia.Fonts.Inter" Version="11.1.*" />
```

Keep `<UseWindowsForms>true</UseWindowsForms>` (tray + MessageBox on the
normal Windows path) and keep the `net8.0-windows` target.

## 2. Replace the body of `RunBrowserFallback`

Find:

```
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
    using var host = new AvaloniaFallbackHost(handler);
    handler.SetBrowserMessageSender(host.SendToBrowser);
    handler.SetBrowserCloseAction(host.RequestShutdown);
    handler.SetBrowserExternalUrlOpener(host.OpenExternalUrl);
    Console.Error.WriteLine("Cypress is using its GameNative/Wine native Avalonia rendering mode.");
    host.Start(); // blocks until the window is closed
    handler.KillAllInstances();
}
```

`ShouldUseBrowserFallback()`, `IsGameNativeSession()`,
`ConfigureWineWebView2Compatibility()` and all `#if WINDOWS` blocks stay
exactly as they are. `BrowserFallbackHost.cs` can stay in the tree as a manual
WebView2 escape hatch; it is simply no longer called on the GameNative path.

</BDS:create_file>

<BDS:create_file fileName="Launcher/README-GameNative-Avalonia.md">

```markdown
# Cypress - GameNative native rendering host (overlay patch)

## What this archive is

This is an **overlay patch**, not a full source tree. It contains only the files
that change on the GameNative/Wine rendering path. Copy them over an existing
`JebbyCodes/Cypress` checkout:

Launcher/CypressLauncher/AvaloniaFallbackHost.cs   (new file)
Launcher/PATCH_Program.cs.diff.md                  (instructions)
Launcher/README-GameNative-Avalonia.md             (this file)

```

Then follow `PATCH_Program.cs.diff.md` (add 4 Avalonia packages, replace the
body of `RunBrowserFallback`). That is the entire code change.

## Why

The launcher serves its UI on loopback and, under Wine/GameNative, displays it
with the downloaded `SimpleWebView2Browser.exe`. That binary is a thin WebView2
(Chromium) host. Under Wine, Chromium's GPU process cannot present its D3D11
shared-texture surface, which produces the white window that only partially
repaints after a click. The loopback server, the message bridge and the HTML are
all correct - the failure is purely the Chromium compositor present step.

`ConfigureWineWebView2Compatibility()` already sets the `win8` AppDefaults
override, and `Program.cs` already guards DPI/`SetWindowPos`. Those are correct
but do not fix a broken present.

## Fix

`AvaloniaFallbackHost.cs` replaces the WebView2 host on the Wine/GameNative path.
It renders with Avalonia + Skia in **software** mode
(`Win32RenderingMode.Software`), so no D3D or DirectComposition is involved. The
existing `MessageHandler` backend is unchanged - the host only replaces the
render and message-pump layer, using the same
`SetBrowserMessageSender` / `HandleMessage(null, json)` /
`SetBrowserExternalUrlOpener` contract the loopback bridge used.

## Status (honest)

- **Not compiled, not run.** There is no .NET SDK, Wine or GameNative in the
  environment that produced this patch. Do not treat it as verified.
- Correct against the reviewed source (`Program.cs`, `BrowserFallbackHost.cs`,
  `core.js`): diagnosis and bridge wiring.
- **Reduced-scope UI.** The native window exercises the real backend (auth,
  instances, browser list) via buttons and a raw-JSON box. It does **not**
  reproduce every tab, the playlist builder, pickers, translations or docs
  pages. Full UI parity requires porting `wwwroot/js/*.js` and
  `wwwroot/css/*.css` to Avalonia views - that work is not included here.
- **EA OAuth not verified.** `EaLoginWindow.cs` was not reviewed. On GameNative
  `OpenExternalUrl` routes to the Android browser; confirm the redirect returns
  into the app on a real device.

## Remaining work

1. Port the full HTML UI to Avalonia for complete feature parity.
2. Confirm every backend action string against `MessageHandler.*.cs` (only
   `checkAuth` is confirmed from `core.js`; the other buttons are illustrative).
3. Verify EA OAuth round-trip on GameNative hardware.
4. Windows 11 regression run.

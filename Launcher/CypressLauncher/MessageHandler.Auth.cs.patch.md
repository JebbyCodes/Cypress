# Patch for MessageHandler.Auth.cs

Replace `OnEaLogin()` and `TryStartEmbeddedEaLogin` usage with the external
browser flow below. Keep every other method (`ExchangeCodeForToken`,
`FetchOwnedGamesFromEAAsync`, `OnRefreshEntitlements`, `OnRelinkEA`, etc.)
unchanged.

## 1. Add the using directives

At the top of the file, inside the existing using block:

```csharp
using System.Net;
using System.Diagnostics;
```

## 2. Replace the body of `OnEaLogin`

Find:

```
private void OnEaLogin()
{
    CleanupLegacyQrcHandler();
    var verifierBytes = new byte[32];
    RandomNumberGenerator.Fill(verifierBytes);
    m_codeVerifier = Base64UrlEncode(verifierBytes);
    byte[] challengeHash = SHA256.HashData(Encoding.ASCII.GetBytes(m_codeVerifier));
    string codeChallenge = Base64UrlEncode(challengeHash);
    string pcSign = GeneratePcSign();
    string authUrl = $"{EA_AUTH_URL}?client_id={EA_CLIENT_ID}" +
        $"&response_type=code" +
        $"&redirect_uri={Uri.EscapeDataString(EA_REDIRECT_URI)}" +
        $"&code_challenge={codeChallenge}" +
        $"&code_challenge_method=S256" +
        $"&pc_sign={Uri.EscapeDataString(pcSign)}";

    if (TryStartEmbeddedEaLogin(authUrl)) return;
    Send(new JObject { ["type"] = "authLoginResult", ["ok"] = false, ["error"] = "embedded ea login is unavailable on this platform" });
}
```

Replace with:

```
private void OnEaLogin()
{
    CleanupLegacyQrcHandler();
    var verifierBytes = new byte[32];
    RandomNumberGenerator.Fill(verifierBytes);
    m_codeVerifier = Base64UrlEncode(verifierBytes);
    byte[] challengeHash = SHA256.HashData(Encoding.ASCII.GetBytes(m_codeVerifier));
    string codeChallenge = Base64UrlEncode(challengeHash);
    string pcSign = GeneratePcSign();
    string authUrl = $"{EA_AUTH_URL}?client_id={EA_CLIENT_ID}" +
        $"&response_type=code" +
        $"&redirect_uri={Uri.EscapeDataString(EA_REDIRECT_URI)}" +
        $"&code_challenge={codeChallenge}" +
        $"&code_challenge_method=S256" +
        $"&pc_sign={Uri.EscapeDataString(pcSign)}";

    // Under Wine/GameNative the embedded WebView2 window cannot render.
    // Open the system browser and catch the qrc:/// redirect with the
    // handler registered by RegisterQrcHandler().
    if (Program.IsBrowserFallback)
    {
        RegisterQrcHandler();
        OpenUrl(authUrl);
        return;
    }

    if (TryStartEmbeddedEaLogin(authUrl)) return;
    Send(new JObject { ["type"] = "authLoginResult", ["ok"] = false, ["error"] = "embedded ea login is unavailable on this platform" });
}
```

## 3. Add the helper methods

Insert these methods inside the `MessageHandler` class, near
`TryStartEmbeddedEaLogin`:

```
// Registers qrc:/// in the Wine prefix so the Android browser hands the
// redirect back to CypressLauncher.exe --qrc "<url>".
private void RegisterQrcHandler()
{
#if WINDOWS
    try
    {
        string exePath = Path.Combine(AppContext.BaseDirectory, "CypressLauncher.exe");
        using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser
            .CreateSubKey(@"Software\Classes\qrc");
        key.SetValue(string.Empty, "URL:Cypress EA Login");
        key.SetValue("URL Protocol", string.Empty);
        using Microsoft.Win32.RegistryKey cmd = key.CreateSubKey(@"shell\open\command");
        cmd.SetValue(string.Empty, $"\"{exePath}\" --qrc \"%1\"");
        Log("Registered qrc:/// handler for external EA login.");
    }
    catch (Exception ex)
    {
        Log("Could not register qrc handler: " + ex.Message);
    }
#endif
}

private static void OpenUrl(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Log("Could not open browser: " + ex.Message);
    }
}

// Called by Program.cs when the launcher is invoked with --qrc <url>.
internal async Task HandleQrcRedirectAsync(string qrcUrl)
{
    try
    {
        Uri uri = new Uri(qrcUrl);
        string? code = System.Web.HttpUtility.ParseQueryString(uri.Query)["code"];
        if (string.IsNullOrEmpty(code))
        {
            Send(new JObject { ["type"] = "authLoginResult", ["ok"] = false, ["error"] = "No authorization code in qrc redirect" });
            return;
        }
        await ExchangeCodeForToken(code, EA_REDIRECT_URI).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Send(new JObject { ["type"] = "authLoginResult", ["ok"] = false, ["error"] = ex.Message });
    }
}
```

## 4. Add `using System.Web;`

The `HttpUtility` call above needs `using System.Web;` at the top of the file.
On .NET 8 this is available via the `System.Web.HttpUtility` assembly, which is
included in the Windows Desktop runtime. If your project does not reference it,
add `<FrameworkReference Include="Microsoft.WindowsDesktop.App" />` to
`CypressLauncher.csproj` or replace `HttpUtility.ParseQueryString` with a small
manual parser:

```
private static string? GetQueryParam(string url, string key)
{
    int q = url.IndexOf('?');
    if (q < 0) return null;
    foreach (string pair in url.Substring(q + 1).Split('&'))
    {
        int eq = pair.IndexOf('=');
        if (eq > 0 && pair.Substring(0, eq) == key)
            return Uri.UnescapeDataString(pair.Substring(eq + 1));
    }
    return null;
}
```

If you use the manual parser, replace `HttpUtility.ParseQueryString(uri.Query)["code"]`
with `GetQueryParam(qrcUrl, "code")`.

</BDS:create_file>

<BDS:create_file fileName="Cypress-GameNative-Fix/Launcher/CypressLauncher/Program.cs.patch.md">

```markdown
# Patch for Program.cs

`Program.cs` must handle the `--qrc` argument that the registered protocol
handler passes back. The current `Main` already has an early branch for CLI
arguments (`PatchCli.Run(args)`). Add a new branch **before** that:

Find:

```csharp
if (args.Length > 0)
{
    AttachConsole(ATTACH_PARENT_PROCESS);
    Console.WriteLine();
    Environment.ExitCode = PatchCli.Run(args);
    FreeConsole();
    return;
}

Replace with:

```
if (args.Length > 0)
{
    // qrc:/// redirect from the external browser EA login flow.
    if (args[0] == "--qrc" && args.Length > 1)
    {
        AttachConsole(ATTACH_PARENT_PROCESS);
        Console.WriteLine();
        var handler = new MessageHandler();
        handler.HandleQrcRedirectAsync(args[1]).GetAwaiter().GetResult();
        FreeConsole();
        return;
    }

    AttachConsole(ATTACH_PARENT_PROCESS);
    Console.WriteLine();
    Environment.ExitCode = PatchCli.Run(args);
    FreeConsole();
    return;
}
```

`HandleQrcRedirectAsync` is internal on `MessageHandler`, so `Program` can call
it because they are in the same assembly.

</BDS:create_file>

<BDS:create_file fileName="Cypress-GameNative-Fix/README.md">

```markdown
# Cypress GameNative fixes

Two independent bugs, two fixes.

## 1. Profile tab has no scrollbar

`content.css` already sets `overflow-y: auto` on `.content`, but the flex child
is missing `min-height: 0`. In a flex column a child defaults to
`min-height: auto`, which means it refuses to shrink below its content height.
Profile is the tallest tab, so `.content` grows to fit it, `body { overflow:
hidden }` clips the overflow, and the scrollbar never appears.

**Fix:** add `min-height: 0` to `.content` (and `.tab-content`). Replace your
`wwwroot/css/content.css` with the one in this archive.

## 2. EA sign-in button does nothing

`OnEaLogin()` calls `TryStartEmbeddedEaLogin`, which opens `EaLoginWindow` — a
**WebView2** form. WebView2 does not work under Wine/Proton; it either shows a
blank window or fails silently in the GPU process. `TryStartEmbeddedEaLogin`
still returns `true` because the task starts, so the JS never receives an error
and the button appears dead.

The redirect URI is `qrc:///html/login_successful.html`, a custom EA protocol
that the Android browser cannot handle.

**Fix:** when `Program.IsBrowserFallback` is true (Wine/GameNative), open the EA
auth URL in the system browser and register a `qrc` protocol handler in the Wine
prefix so the redirect comes back to `CypressLauncher.exe --qrc "<url>"`.

Apply:

1. Replace `Launcher/wwwroot/css/content.css` with the file from this archive.
2. Apply `Launcher/CypressLauncher/MessageHandler.Auth.cs.patch.md` to your
   `MessageHandler.Auth.cs`.
3. Apply `Launcher/CypressLauncher/Program.cs.patch.md` to your `Program.cs`.
4. Rebuild.

## Status

- **Not compiled or run in this environment.** No .NET SDK or Wine prefix is
  available here. The CSS fix is certain; the EA flow depends on EA accepting
  the `qrc` redirect from the system browser, which is how EA Desktop itself
  works[reference:4].
- **qrc handler:** the patch registers `HKCU\Software\Classes\qrc` pointing at
  `CypressLauncher.exe --qrc "%1"`. Under GameNative the Android browser will
  open the `qrc:///` URL by handing it to the Wine shell, which invokes that
  command. If your GameNative build strips protocol handlers, this will not
  work and the only remaining option is a loopback redirect, which EA does not
  document for `JUNO_PC_CLIENT`.
- **Windows 11:** `Program.IsBrowserFallback` is false there, so the existing
  WebView2 `EaLoginWindow` path is unchanged.


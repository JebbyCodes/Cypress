# Patch for `MessageHandler.Auth.cs` - external EA login on native fallback

The embedded EA sign-in window (`EaLoginWindow`) is a WebView2 form. Under
Wine/GameNative WebView2 fails the same way the main UI did, so the popup never
appears. When `Program.IsBrowserFallback` is true, use the system browser and a
loopback redirect instead of `EaLoginWindow`.

Add `using System.Net;` at the top of the file.

## 1. Change `OnEaLogin` to take the external path on fallback

Find:

```csharp
if (TryStartEmbeddedEaLogin(authUrl)) return;
Send(new JObject { ["type"] = "authLoginResult", ["ok"] = false, ["error"] = "embedded ea login is unavailable on this platform" });
```

Replace with:

```
if (Program.IsBrowserFallback)
{
    _ = StartExternalEaLogin(pcSign);
    return;
}

if (TryStartEmbeddedEaLogin(authUrl)) return;
Send(new JObject { ["type"] = "authLoginResult", ["ok"] = false, ["error"] = "embedded ea login is unavailable on this platform" });
```

## 2. Add the external login method

Insert this method inside the `MessageHandler` class (near `TryStartEmbeddedEaLogin`):

```
// External-browser PKCE flow used on Wine/GameNative where WebView2 cannot
// render. Opens the system browser, listens on loopback for the redirect and
// exchanges the authorization code for a token. No WebView2 is involved.
private async Task StartExternalEaLogin(string pcSign)
{
    HttpListener? listener = null;
    try
    {
        if (m_codeVerifier == null)
        {
            byte[] verifierBytes = new byte[32];
            RandomNumberGenerator.Fill(verifierBytes);
            m_codeVerifier = Base64UrlEncode(verifierBytes);
        }

        byte[] challengeHash = SHA256.HashData(Encoding.ASCII.GetBytes(m_codeVerifier));
        string codeChallenge = Base64UrlEncode(challengeHash);

        int port = GetFreeLoopbackPort();
        string redirectUri = $"http://127.0.0.1:{port}/ea-callback";

        string authUrl = $"{EA_AUTH_URL}?client_id={EA_CLIENT_ID}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&code_challenge={codeChallenge}" +
            $"&code_challenge_method=S256" +
            $"&pc_sign={Uri.EscapeDataString(pcSign)}";

        listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        Log("External EA login: opening system browser at " + EA_AUTH_URL);

        if (m_externalUrlOpener != null)
            m_externalUrlOpener(authUrl);
        else
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        HttpListenerContext context = await listener.GetContextAsync()
            .WaitAsync(TimeSpan.FromMinutes(5))
            .ConfigureAwait(false);

        string? code = context.Request.QueryString["code"];
        string? error = context.Request.QueryString["error"];

        byte[] page = Encoding.UTF8.GetBytes(
            "<html><body style='font-family:sans-serif;padding:2em'>" +
            (code != null
                ? "<h2>Sign-in successful</h2><p>You can close this window and return to Cypress.</p>"
                : "<h2>Sign-in failed</h2><p>" + (error ?? "No authorization code received.") + "</p>") +
            "</body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = page.Length;
        await context.Response.OutputStream.WriteAsync(page, 0, page.Length).ConfigureAwait(false);
        context.Response.Close();

        if (string.IsNullOrEmpty(code))
        {
            Send(new JObject { ["type"] = "authLoginResult", ["ok"] = false, ["error"] = error ?? "No authorization code received" });
            return;
        }

        await ExchangeCodeForToken(code, redirectUri).ConfigureAwait(false);
    }
    catch (TimeoutException)
    {
        Send(new JObject { ["type"] = "authLoginResult", ["ok"] = false, ["error"] = "EA login timed out" });
    }
    catch (Exception ex)
    {
        Send(new JObject { ["type"] = "authLoginResult", ["ok"] = false, ["error"] = ex.Message });
    }
    finally
    {
        try { listener?.Stop(); } catch { }
        try { listener?.Close(); } catch { }
    }
}

private static int GetFreeLoopbackPort()
{
    var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    l.Start();
    int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return port;
}
```

## 3. Wire the external URL opener from the native host

In `Program.RunBrowserFallback`, make sure this line is present **before**
`host.Start()`:

```
handler.SetBrowserExternalUrlOpener(host.OpenExternalUrl);
```

That is what lets `StartExternalEaLogin` open the system browser. The native
host already implements `OpenExternalUrl`.

## 4. (If not already done) correct the native host's EA button

The native host's EA Login button must send `{"type":"eaLogin"}` (the same
action `auth.js` sends with `send('eaLogin')`), not `startEALogin`. The updated
`NativeFallbackHost.cs` in this archive already does this.

## 5. `m_externalUrlOpener` visibility

`StartExternalEaLogin` uses `m_externalUrlOpener`. That field already exists in
`MessageHandler` because `SetBrowserExternalUrlOpener` assigns it. If it is
private in a different partial file, no change is needed because this method is
in the same class. If the compiler complains, make sure the field is declared
as `private Action<string>? m_externalUrlOpener;` in `MessageHandler.Core.cs`
or wherever it already lives.

</BDS:create_file>

<BDS:create_file fileName="Launcher/README-GameNative-EA-Login.md">

```markdown
# EA login fix for the native (WinForms) fallback

## Symptom

The launcher renders correctly in native mode, but clicking EA Login never shows
a sign-in popup.

## Cause

`OnEaLogin` calls `TryStartEmbeddedEaLogin`, which opens `EaLoginWindow` - a
`WebView2` form. Under Wine/GameNative WebView2 cannot present its surface, so
the window fails to appear or fails silently, exactly like the main UI did before
the native host fix. The problem is not the auth logic; it is the WebView2
renderer inside the login window.

## Fix

When `Program.IsBrowserFallback` is true, `OnEaLogin` now uses
`StartExternalEaLogin` instead of `EaLoginWindow`:

1. Generates a PKCE verifier/challenge (same as before).
2. Starts a short-lived `HttpListener` on a free loopback port.
3. Opens the EA authorization URL in the system browser (via
   `handler.SetBrowserExternalUrlOpener(host.OpenExternalUrl)`).
4. Waits for EA to redirect to `http://127.0.0.1:<port>/ea-callback`.
5. Shows a "you can close this window" page, then exchanges the code for a token
   with the same redirect URI.

No WebView2 is involved, so the Wine compositor failure cannot occur.

## Apply

1. Replace `Launcher/CypressLauncher/NativeFallbackHost.cs`.
2. Follow `Launcher/PATCH_MessageHandler.Auth.cs.md`.
3. Ensure `Program.RunBrowserFallback` calls
   `handler.SetBrowserExternalUrlOpener(host.OpenExternalUrl)` before
   `host.Start()`.

## Status (honest)

- Not compiled or run here (no .NET SDK/Wine/GameNative in this environment).
- The redirect-URI change (`qrc:///html/login_successful.html` ->
  `http://127.0.0.1:<port>/ea-callback`) must be accepted by EA's OAuth client
  `JUNO_PC_CLIENT`. Loopback redirects are standard for native apps, but this
  specific client may reject them; if it does, the token exchange will return an
  error and the launcher will display it. That is the first thing to check on
  real hardware.
- If EA rejects the loopback redirect, the fallback is to register a `qrc`
  protocol handler in Wine that hands the code back to the launcher. That is a
  larger change and is not included here.


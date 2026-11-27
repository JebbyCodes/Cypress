# Patch for `Launcher/CypressLauncher/MessageHandler.Core.cs`

Two edits. Both are inside the `switch (type)` in `HandleMessage`.

## 1. Route `selectGameDir` through the STA-safe picker

Find (around line 98):

```csharp
                case "selectGameDir":
                    OnSelectGameDir();
                    break;
```

Replace with:

```
                case "selectGameDir":
                    // FolderBrowserDialog needs an STA thread. HandleMessage runs on
                    // the WebView2 thread, where the dialog throws and the outer
                    // catch swallows it, so Browse looked dead. Try the STA helper
                    // first and only fall back to the original implementation.
                    if (!TryPickGameDirectory())
                        OnSelectGameDir();
                    break;
```

## 2. Add `TryPickGameDirectory` and a generic `browseFile` action

Append these members to the class (anywhere inside `public partial class MessageHandler`):

```
    // Returns true when the request was fully handled (including "user
    // cancelled"), false when the caller should use the legacy path.
    private bool TryPickGameDirectory()
    {
        try
        {
            string? picked = NativeDialogs.PickFolder(
                "Select the game installation folder", m_gameDirectory);

            // Cancelled: handled, do nothing else.
            if (picked == null)
                return true;

            m_gameDirectory = picked;
            SaveUserData(null);
            Send(new JObject { ["type"] = "gameDir", ["path"] = picked });
            return true;
        }
        catch (Exception ex)
        {
            SendStatus("Folder picker failed: " + ex.Message, "error");
            return false;
        }
    }

    // Generic file picker used by inputs that need a real filesystem path
    // (the browser cannot supply one, so the backend has to do it).
    private void OnBrowseFile(JObject msg)
    {
        try
        {
            string title = (string?)msg["title"] ?? "Select a file";
            string filter = (string?)msg["filter"] ?? "All files (*.*)|*.*";
            string? current = (string?)msg["current"];
            string? picked = NativeDialogs.PickFile(title, filter, current);
            Send(new JObject
            {
                ["type"] = "browseFileResult",
                ["requestId"] = (string?)msg["requestId"] ?? "",
                ["path"] = picked ?? ""
            });
        }
        catch (Exception ex)
        {
            Send(new JObject
            {
                ["type"] = "browseFileResult",
                ["requestId"] = (string?)msg["requestId"] ?? "",
                ["path"] = "",
                ["error"] = ex.Message
            });
        }
    }
```

Then add one more case to the switch, next to `autoFindDir`:

```
                case "browseFile":
                    OnBrowseFile(msg);
                    break;
```

## 3. Optional: frontend helper for `browseFile`

Append to `Launcher/wwwroot/js/helpers.js`:

```
// cypress-fix5: backend file picker for inputs that need a real path.
var _browseResolvers = {};
function browseFileInto(inputId, title, filter) {
    var input = document.getElementById(inputId);
    var requestId = 'b' + Date.now() + Math.floor(Math.random() * 1000);
    return new Promise(function (resolve) {
        _browseResolvers[requestId] = resolve;
        send('browseFile', {
            requestId: requestId,
            title: title || 'Select a file',
            filter: filter || 'All files (*.*)|*.*',
            current: input ? input.value : ''
        });
    });
}
```

And in the `core.js` `receiveMessage` switch, next to `case 'browserIcon':`:

```
                case 'browseFileResult':
                    if (_browseResolvers[data.requestId]) {
                        _browseResolvers[data.requestId](data.path || '');
                        delete _browseResolvers[data.requestId];
                        if (data.error) showStatus(data.error, 'error');
                    }
                    break;
```

</BDS:create_file>

<BDS:create_file fileName="Cypress-Fix5/README.md">

```markdown
# Cypress fixes round 5

Four independent bugs, four causes, four fixes. Unlike the previous rounds these
are **append-only patches plus one HTML insertion**, driven by a script, so no
file can be truncated.

## Apply

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\Cypress-Fix5\apply-patches.ps1

Then apply the two manual patches:

1. `Launcher/CypressLauncher/MessageHandler.Core.cs.patch.md`
2. Copy `Launcher/CypressLauncher/NativeDialogs.cs` into
`Launcher/CypressLauncher/`

Rebuild. The HTML/CSS/JS are embedded resources, so a rebuild is required for
the frontend changes to take effect.

## 1. No scrollbar on the right pane

**Real cause:** `base.css` has `body { zoom: 1.1 }`. `.app` is
`height: calc(100vh - 0px)` and `layout.css` also sets `overflow: hidden` on
`html, body`. Inside a zoomed body, `100vh` becomes 110% of the real viewport,
so `.app` is taller than the window, the bottom 10% is clipped, and `.content`'s
scrollbar is drawn *below the visible area*. Every `min-height: 0` fix in the
previous rounds was applied to a box that was already off-screen.

**Fix:** `body { zoom: 1 !important; font-size: 16.5px; }` keeps the original
visual size without the broken viewport maths.

**Second cause:** `.tab-content { flex: 1 }` inside `.content` means it is sized
to the leftover space, not to its content, so it never produces overflow.
`.content > .tab-content { flex: 0 0 auto; }` makes it take its natural height,
which is what gives `.content` something to scroll.

## 2. Raw translation keys (`JOIN.SERVER_ADDRESS_LABEL`)

`window.t` in `i18n.js` returns the key verbatim when the lookup misses:

```
var str = window._i18nStrings[key];
if (str === undefined) return key;
```

The fix wraps `t` with an exact → lowercase → uppercase → humanised fallback, so
a missing key renders as "Server Address Label" instead of
`JOIN.SERVER_ADDRESS_LABEL`. This also fixes every other casing mismatch at
once, which is better than editing individual call sites.

## 3. EA sign-in button missing

The only "Sign in with EA" control lives inside `profileSignedOutBlock`, which
is hidden in most states. The script inserts a `#sidebarEaLoginBtn` immediately
before `<div class="nav-links">` (right under the sidebar profile widget) and
appends a small `auth.js` block that hides it when `window._eaLoggedIn` is true.
It calls the existing `showAuthModal()`, so the `eaLogin` backend path is
unchanged.

## 4. Browse buttons do nothing

`selectGameDir` calls `OnSelectGameDir()`, which opens a
`FolderBrowserDialog`. That is a COM dialog and requires an STA thread;
`HandleMessage` is invoked on the WebView2 message thread, so the dialog throws
`ThreadStateException`, and the dispatcher's outer `catch (Exception ex)` turns
it into a status message that most users never see.

**Fix:** `NativeDialogs` runs the picker on a dedicated STA thread with a
timeout. `selectGameDir` tries that first and only falls back to
`OnSelectGameDir()` if it fails. The same helper powers a new `browseFile`
action for any input that needs a real filesystem path.

## Status — read this

- **Not compiled or run by me.** No .NET SDK, Wine or WebView2 in this
environment.
- The `zoom` diagnosis is direct from `base.css` (`body { ... zoom: 1.1 }`) and
`layout.css` (`.app { height: calc(100vh - 0px) }`); it explains why the
previous `min-height` patches had no visible effect.
- The `ThreadStateException` diagnosis follows from `FolderBrowserDialog`
requiring STA while `HandleMessage` is called from the WebView2 thread; the
outer `catch` in `HandleMessage` is why the failure is silent.
- `MessageHandler.Core.cs.patch.md` is a **manual** patch because the exact
`selectGameDir` case must be replaced in place. The file you have may differ
slightly if you have local edits.
- If a scrollbar still does not appear after the `zoom` fix, check whether your
build embeds `wwwroot` at all — `ResourceBuilder.BuildHtml` inlines the CSS,
so editing the files without rebuilding changes nothing.


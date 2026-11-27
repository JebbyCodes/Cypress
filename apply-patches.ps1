# Run from the repository root (the folder containing Launcher/).
# Applies every fix in this archive. Idempotent: safe to run twice.
$ErrorActionPreference = 'Stop'
$root = Get-Location

function Append-Once($file, $marker, $block) {
    if (-not (Test-Path $file)) { Write-Warning "Missing: $file"; return }
    $text = Get-Content -Raw $file
    if ($text -like "*$marker*") { Write-Host "skip (already applied): $file"; return }
    Add-Content -Path $file -Value "`r`n$block" -Encoding UTF8
    Write-Host "patched: $file"
}

# 1. Remove body zoom (the real cause of the missing scrollbar)
Append-Once "$root\Launcher\wwwroot\css\base.css" `
    "/* cypress-fix5: zoom" `
    @'
/* cypress-fix5: zoom disabled.
   body{zoom:1.1} made .app (height:100vh) render at 110% of the viewport.
   html,body{overflow:hidden} then clipped the bottom and pushed .content's
   scrollbar below the visible window. Keep the visual size via font-size. */
body { zoom: 1 !important; font-size: 16.5px; }
'@

# 2. Make tab content size to its content so .content overflows and scrolls
Append-Once "$root\Launcher\wwwroot\css\content.css" `
    "cypress-fix5: tab sizing" `
    @'
/* cypress-fix5: tab sizing.
   .tab-content had flex:1, so it was sized to the leftover space instead of
   its own content and never produced overflow. flex:0 0 auto makes it take
   its natural height, which is what gives .content a scrollbar. */
.content > .tab-content { flex: 0 0 auto; min-height: 0; }
.content > #updateBannerContainer { flex: 0 0 auto; }
.tab-content.active > * { flex-shrink: 0; }
'@

# 3. i18n fallback so raw keys never render
Append-Once "$root\Launcher\wwwroot\js\i18n.js" `
    "cypress-fix5: i18n fallback" `
    @'
/* cypress-fix5: i18n fallback.
   t() returned the raw key when a lookup missed, so JOIN.SERVER_ADDRESS_LABEL
   was printed verbatim. Try exact, then lowercase, then a humanised form. */
(function () {
    var raw = window.t;
    window.t = function (key, vars) {
        var s = window._i18nStrings || {};
        if (s[key] !== undefined) return raw(key, vars);
        var lower = String(key).toLowerCase();
        if (s[lower] !== undefined) return raw(lower, vars);
        var upper = String(key).toUpperCase();
        if (s[upper] !== undefined) return raw(upper, vars);
        var human = String(key)
            .replace(/[._]+/g, ' ')
            .replace(/\s+/g, ' ')
            .trim()
            .replace(/\b\w/g, function (c) { return c.toUpperCase(); });
        return human || String(key);
    };
})();
'@

# 4. Persistent EA sign-in button in the sidebar + visibility tracking
$authJs = "$root\Launcher\wwwroot\js\auth.js"
Append-Once $authJs "cypress-fix5: sidebar ea button" @'
/* cypress-fix5: sidebar EA button visibility. */
(function () {
    function sync() {
        var b = document.getElementById('sidebarEaLoginBtn');
        if (b) b.style.display = window._eaLoggedIn ? 'none' : '';
    }
    if (typeof updateProfileIdentitySection === 'function' &&
        !updateProfileIdentitySection.__eaSidebar) {
        var orig = updateProfileIdentitySection;
        window.updateProfileIdentitySection = function () {
            orig.apply(this, arguments); sync();
        };
        window.updateProfileIdentitySection.__eaSidebar = true;
    }
    document.addEventListener('DOMContentLoaded', sync);
    setTimeout(sync, 1200);
})();
'@

# 5. Insert the sidebar button markup
$html = "$root\Launcher\wwwroot\index.html"
if (Test-Path $html) {
    $t = Get-Content -Raw $html
    if ($t -notlike '*sidebarEaLoginBtn*') {
        $btn = @'
            <button class="btn btn-primary" id="sidebarEaLoginBtn"
                    onclick="showAuthModal()"
                    style="margin:4px 12px 10px;width:calc(100% - 24px);">
                <span data-i18n="auth.login_btn">Sign in with EA</span>
            </button>
'@
        $marker = '<div class="nav-links">'
        $i = $t.IndexOf($marker)
        if ($i -ge 0) {
            $t = $t.Substring(0, $i) + $btn + "`r`n            " + $t.Substring($i)
            Set-Content -Path $html -Value $t -Encoding UTF8 -NoNewline
            Write-Host "patched: $html (inserted sidebarEaLoginBtn)"
        } else {
            Write-Warning "Could not find <div class=\"nav-links\"> in index.html; insert the button manually."
        }
    } else { Write-Host "skip (already applied): $html" }
}

Write-Host "`nDone. Rebuild the launcher for the embedded resources to pick up the changes."

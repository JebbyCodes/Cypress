# Cypress fixes round 3

## 1. Game images enlarged

My previous `content.css` was truncated at `/* ... rest of content.css unchanged ... */`.
That deleted `.game-info-panel`, `.game-info-bg`, `.game-info-icon`,
`.game-info-overlay`, and every `.changed-settings-*` rule, so the game `<img>`
elements rendered at natural size. The full file is restored in this archive and
only two properties were added (`min-height: 0`).

**Apply:** replace `Launcher/wwwroot/css/content.css` with this one.

## 2. Scrollbar still missing

`layout.css` shows `.app { height: calc(100vh - var(--titlebar-height)); display: flex }`.
`--titlebar-height` is `0px` (base.css), so `.app` is exactly `100vh` — the parent
*is* bounded. The remaining break is that `.tab-content` (the direct child of
`.content`, holding the Profile form) has `flex: 1` but no `min-height: 0`. In a
flex column that makes it refuse to shrink below its content, so `.content` never
sees overflow and never shows a scrollbar. Adding `min-height: 0` to both
`.content` and `.tab-content`, plus `overflow: hidden` on `.app`, closes the
chain.

**Apply:** replace `layout.css` and `content.css`.

If a scrollbar still does not appear after this, the next thing to check is
whether `.content` is a direct child of `.app`. If there is an intermediate
wrapper between them, that wrapper also needs `min-height: 0` and
`overflow: hidden`.

## 3. Persistent EA sign-in button

The startup auth modal can be dismissed and there is no obvious way to reopen it.
`auth.js.append.js` adds a fixed "Sign in with EA" button that is visible
whenever `window._eaLoggedIn` is false and calls the existing `showAuthModal()`.

**Apply:** append the contents of `Launcher/wwwroot/js/auth.js.append.js` to the
end of `Launcher/wwwroot/js/auth.js`. No other file changes are needed — the
button reuses the existing `eaLogin` backend action.

## Status

- **Not compiled or run in this environment.** No .NET SDK, Wine or WebView2
  here.
- The CSS fixes are certain against the source I read (`base.css`, `layout.css`,
  `content.css`). The restored `content.css` is the original file plus two added
  properties, so the game-image regression is guaranteed to be undone.
- The EA button reuses the existing modal and the existing `eaLogin` action, so
  it does not introduce a new auth path. Whether `eaLogin` actually opens a
  working sign-in window under GameNative still depends on the qrc/loopback
  backend patch from the previous round.

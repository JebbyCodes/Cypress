# Cypress UI fixes

This source snapshot includes a set of fixes for the launcher UI:

## Right-hand content scrollbar

- The main content pane uses a dedicated vertical scroll container and reserves a visible scrollbar rail.
- The scrollbar thumb has a stronger contrast in both Chromium/WebView2 and Firefox-style rendering.
- The active tab is allowed to grow with its content instead of being compressed into the available viewport height.
- The app uses the dynamic viewport height where supported, with the regular viewport-height declaration as a fallback.
- The page-wide `zoom` setting has been removed because it scaled the viewport-sized layout beyond the visible window. The inherited body text size has been adjusted to retain the previous text scale.

## EA sign-in compatibility

The sidebar **Sign in with EA** entry point reuses Cypress's EA authorization-code flow with PKCE. Standard Windows builds use a dedicated WebView2 sign-in window with a persistent, isolated browser profile, working context menus, visible navigation status, initialization timeouts, and callback handling that avoids logging authorization codes.

Wine/GameNative builds skip the fragile second WebView2 window. They open the official EA sign-in URL in the system browser, then show a Cypress-owned callback form. EA's `qrc:///html/login_successful.html` redirect is not a normal Android app-link, so GameNative's external browser cannot return the code directly to the Cypress process. After signing in, the user copies the full return URL from the browser address bar into the callback form; Cypress extracts the one-time code and completes the PKCE token exchange. EA passwords are entered only on EA's page, never in Cypress.

## Native file pickers

The game-directory **Browse** control and the server-icon **Choose Image** control use Cypress's own WinForms path pickers on Windows/Wine instead of relying on Windows shell dialogs or the embedded browser's HTML file input. The folder picker accepts typed paths and validates that the selected directory contains the expected game executable. The image picker filters common image formats and resizes the selected icon to a 64×64 JPEG before returning it to the UI.

## Localisation key handling

The translation resolver now normalises legacy uppercase keys (for example, `JOIN.SERVER_ADDRESS_LABEL`) and tries the matching locale-catalogue key before falling back. When a key is genuinely missing, it displays a readable label instead of exposing the raw translation identifier. Text, placeholders, titles, and tooltip attributes all use the same resolver.

## Build and validation

The frontend is embedded into the launcher by `CypressLauncher/ResourceBuilder.cs`, so rebuild the launcher after changing these files.

Static validation completed:

- JavaScript syntax checks passed for all frontend scripts.
- CSS parsing checks passed for all frontend stylesheets.
- Translation JSON files parse successfully.
- The uppercase-key lookup, missing-key fallback, and interpolation behaviour passed targeted tests.

A full .NET build and live GameNative/EA sign-in smoke test could not be run in this environment because the .NET SDK and GameNative runtime were unavailable. JavaScript syntax and source-level integration checks are performed on the updated frontend and backend files before packaging.

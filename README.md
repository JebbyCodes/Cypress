# Cypress UI fixes

This source snapshot includes a set of fixes for the launcher UI:

## Right-hand content scrollbar

- The main content pane uses a dedicated vertical scroll container and reserves a visible scrollbar rail.
- The scrollbar thumb has a stronger contrast in both Chromium/WebView2 and Firefox-style rendering.
- The active tab is allowed to grow with its content instead of being compressed into the available viewport height.
- The app uses the dynamic viewport height where supported, with the regular viewport-height declaration as a fallback.
- The page-wide `zoom` setting has been removed because it scaled the viewport-sized layout beyond the visible window. The inherited body text size has been adjusted to retain the previous text scale.

## Persistent EA sign-in entry point

A dedicated **Sign in with EA** button now appears in the sidebar below the profile widget. It opens the existing authentication modal through `showAuthModal()` and reuses the existing `eaLogin` backend action. The button is hidden after a successful sign-in and remains available when signed out; in the collapsed sidebar, its icon remains visible.

## Localisation key handling

The translation resolver now normalises legacy uppercase keys (for example, `JOIN.SERVER_ADDRESS_LABEL`) and tries the matching locale-catalogue key before falling back. When a key is genuinely missing, it displays a readable label instead of exposing the raw translation identifier. Text, placeholders, titles, and tooltip attributes all use the same resolver.

## Build and validation

The frontend is embedded into the launcher by `CypressLauncher/ResourceBuilder.cs`, so rebuild the launcher after changing these files.

Static validation completed:

- JavaScript syntax checks passed for all frontend scripts.
- CSS parsing checks passed for all frontend stylesheets.
- Translation JSON files parse successfully.
- The uppercase-key lookup, missing-key fallback, and interpolation behaviour passed targeted tests.

A full .NET build and in-app WebView2 smoke test could not be run in this environment because the .NET SDK was unavailable and the headless browser was blocked. The existing EA authentication backend was not changed.

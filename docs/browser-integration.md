# Browser integration

Edge and Chrome are paired separately. Each browser can point to one Oshi Tuner directory for the current Windows account. For example, Edge can use the installed copy while Chrome uses a development build.

## Pair a browser

1. Run the Oshi Tuner copy you want to pair.
2. Open **Settings > Browser Integration** and select **Pair** beside Edge or Chrome. If another copy already owns that browser, **Switch to this install** asks before replacing the pairing.
3. Install the extension in that browser. Store listings are pending. For development verification, enable developer mode in `edge://extensions` or `chrome://extensions`, choose **Load unpacked**, and select the matching `browser/edge` or `browser/chrome` directory inside the build or installed application folder.

During an upgrade, setup briefly pauses the browser registrations for the installation being replaced and restores them afterward. Open browsers reconnect automatically. If a browser helper is locking a development build, MSBuild reports the locked executable before trying to copy files.

The optional installer setup can pair an unpaired browser. Use Settings to move a pairing already held by another copy. Uninstalling a copy removes only the pairings it owns.

## Watching Twitch

The WATCH column shows **Watching** for a live channel with an open Twitch channel tab in either paired browser. Watching rows sort first. The browser icons appear on live rows without an open tab. Tab changes update the grid independently of Twitch auto-refresh. A selected Twitch channel can add a temporary row, which stays until the last tab for that channel closes. Choose an available Edge or Chrome icon to open a live channel in its own managed window. The per-browser limit counts managed tabs across every window in that browser, including tabs opened from Twitch Status. Oshi switches ranked streams in the same managed tab and window. To manage an existing stream, put it in a window with one Twitch channel tab, then right-click its Twitch Status row and choose **Manage open stream** for the paired browser.

Reload the unpacked extension after updating the application. The bridge only reads Twitch channel tabs in the browser profile running the extension.

## Twitch player recovery

When a Twitch channel player displays the English video download cancellation prompt with **Error #1000**, the extension clicks **Reload Player** in that tab. It makes one more attempt after 20 seconds if the same prompt persists. This recovery runs within the affected tab.

## Gifted channel discovery

When the paired application requests discovery, the extension reads candidate Twitch channel logins from an existing Gifted Subscriptions tab. It leaves the active tab, window focus and other pages alone. The app must verify candidate subscriptions through its own Twitch sign-in. The bridge sends candidate logins and status only.

The native messaging protocol uses a version and request ID. Version 1 accepts `gift_channels` and replies with a status (`complete`, `partial`, `signed_out`, or `unavailable`) and a list of logins. Until the rendered page can establish completeness, the extension reports results with candidates as partial. A closed browser or a missing gift tab is unavailable. Native messaging registrations are browser-specific and allow only the matching extension ID. The already running app uses a current-user-only named pipe scoped to its installation.

After pairing, the extension reconnects on browser startup and periodically while the browser runs. If you enable **Settings > Auto Launch**, it can wake the paired app to the system tray and start Oshi Watch after the configured cooldown. A browser that is paired to a different copy leaves the previous native host automatically. Check the paired path and that the browser has the extension loaded if the application cannot reach it.

Local API clients can call `get_browser_pairings`, `set_browser_pairing`, `open_browser_integration` and `close_browser_integration` through JSON-RPC or MCP. Explicitly confirm with the user before passing `replace: true` to switch the paired installation.

(() => {
  const playerSelector = '[data-a-target="video-player"], [data-test-selector="video-player"], .video-player';
  const adSelector = '[data-a-target="video-ad-label"], [data-a-target="video-ad-countdown"], ' +
    '[data-a-target="video-ad-overlay"], [data-test-selector="ad-banner-default-text"], [aria-label="Advertisement" i]';
  const adBreakText = /\bis taking an ad break\s*;\s*stick around to support the stream\b/i;
  const controlSelector = 'button, [role="button"], a[href]';
  const nonChannelRoutes = new Set(["activate", "bits", "browse", "checkout", "creatorcamp", "dashboard",
    "directory", "downloads", "drops", "friends", "inventory", "login", "messages", "p", "popout",
    "prime", "products", "search", "settings", "signup", "store", "subscriptions", "turbo", "videos", "wallet"]);
  const retryDelayMs = 20_000;
  const maxAttempts = 2;
  let route;
  let attempts = 0;
  let nextClick = 0;
  let scanTimer;
  let retryTimer;

  function channelRoute() {
    const login = /^\/([a-z0-9_]{1,25})\/?$/i.exec(location.pathname)?.[1].toLowerCase();
    return login && !nonChannelRoutes.has(login) ? login : undefined;
  }

  function normalizedText(value) {
    return (value ?? "").replace(/\s+/g, " ").trim();
  }

  function isVisible(element) {
    if (element.closest('[hidden], [aria-hidden="true"]') || !element.getClientRects().length) return false;
    if (element.checkVisibility) return element.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true });
    const style = getComputedStyle(element);
    return style.visibility === "visible" && style.opacity !== "0";
  }

  function adIsVisible() {
    if ([...document.querySelectorAll(adSelector)].some(isVisible)) return true;
    return [...document.querySelectorAll(playerSelector)].some(player => isVisible(player) &&
      adBreakText.test(normalizedText((player.parentElement ?? player).innerText)));
  }

  function findReloadControl() {
    for (const player of document.querySelectorAll(playerSelector)) {
      if (!isVisible(player)) continue;
      for (const control of player.querySelectorAll(controlSelector)) {
        if (normalizedText(control.innerText || control.getAttribute("aria-label")).toLowerCase()
            !== "click here to reload player" || !isVisible(control)) continue;

        const href = control.tagName === "A" ? control.getAttribute("href") : null;
        if (href && !href.startsWith("#")) continue;
        for (let prompt = control.parentElement; player.contains(prompt); prompt = prompt.parentElement) {
          const text = normalizedText(prompt.innerText);
          if (isVisible(prompt) && /\berror\s*#1000\b/i.test(text)
              && /the video download was cancelled\.\s*please try again\./i.test(text)) return control;
        }
      }
    }
    return null;
  }

  function observeReload(player, login) {
    const runtime = globalThis.chrome?.runtime;
    if (!runtime?.sendMessage || !globalThis.crypto?.randomUUID || !player) return;
    const id = crypto.randomUUID().replaceAll("-", "");
    const report = phase => {
      try { void runtime.sendMessage({ type: "player_recovery", id, phase }).catch(() => {}); }
      catch { /* The extension was reloaded during playback. */ }
    };
    report("attempted");
    const startedAt = performance.now();
    const deadline = startedAt + 15_000;
    let previousVideo;
    let previousTime;
    function check() {
      if (channelRoute() !== login || performance.now() >= deadline || adIsVisible()) {
        report("unconfirmed");
        return;
      }
      const video = player.isConnected ? player.querySelector("video")
        : document.querySelectorAll(playerSelector)[0]?.querySelector("video");
      if (video && !video.paused && !video.seeking && video.readyState >= 2 &&
          video === previousVideo && video.currentTime > previousTime + 0.1 && !findReloadControl() &&
          performance.now() - startedAt >= 2500) {
        report("recovered");
        return;
      }
      previousVideo = video;
      previousTime = video?.currentTime;
      setTimeout(check, 500);
    }
    setTimeout(check, 500);
  }

  function scan() {
    clearTimeout(retryTimer);
    const current = channelRoute();
    if (current !== route) {
      route = current;
      attempts = 0;
      nextClick = 0;
    }

    if (route && adIsVisible()) return;
    const control = route ? findReloadControl() : null;
    if (!control) {
      attempts = 0;
      return;
    }
    if (attempts >= maxAttempts || control.matches(':disabled, [aria-disabled="true"]')) return;
    const remaining = nextClick - performance.now();
    if (remaining > 0) {
      retryTimer = setTimeout(scan, remaining);
      return;
    }
    ++attempts;
    nextClick = performance.now() + retryDelayMs;
    observeReload(control.closest?.(playerSelector), route);
    control.click();
    if (attempts < maxAttempts) retryTimer = setTimeout(scan, retryDelayMs);
  }

  function scheduleScan() {
    if (scanTimer !== undefined) return;
    scanTimer = setTimeout(() => {
      scanTimer = undefined;
      scan();
    }, 120);
  }

  function affectsPlayer(record) {
    const target = record.target instanceof Element ? record.target : record.target.parentElement;
    const watched = playerSelector + ", " + adSelector;
    if (target?.closest(watched) || target?.parentElement?.querySelector(playerSelector) ||
        (record.type === "attributes" && target?.querySelector(watched))) return true;
    for (const list of [record.addedNodes, record.removedNodes]) {
      for (const node of list) {
        if (node instanceof Element && (node.matches(watched) || node.querySelector(watched))) return true;
      }
    }
    return false;
  }

  new MutationObserver((records) => {
    if (records.some(affectsPlayer)) scheduleScan();
  }).observe(document.documentElement, {
    subtree: true, childList: true, characterData: true, attributes: true,
    attributeFilter: ["aria-hidden", "aria-disabled", "aria-label", "hidden", "disabled", "class", "style", "data-a-target", "data-test-selector"]
  });
  addEventListener("popstate", scheduleScan);
  window.navigation?.addEventListener("navigatesuccess", scheduleScan);
  scheduleScan();
})();

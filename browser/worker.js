const hosts = {
  cagcflabpbciehhnjchjipdipmgplipl: "com.oshituner.edge",
  hnomfkgbacjeiocjcgbnnoaaloleefjf: "com.oshituner.chrome",
};
const host = hosts[chrome.runtime.id];
let connection;
let tabChange;
let lastWatchSignature;
const channelLoginPattern = /^[a-z0-9_]{1,25}$/i;
const nonChannelRoutes = new Set(["activate", "bits", "browse", "checkout", "creatorcamp", "dashboard",
  "directory", "downloads", "drops", "friends", "inventory", "login", "messages", "p", "popout",
  "prime", "products", "search", "settings", "signup", "store", "subscriptions", "turbo", "videos", "wallet"]);

function channelLogin(tab) {
  const location = tab.pendingUrl ?? tab.url;
  if (!location) return undefined;
  const url = new URL(location);
  if (url.origin !== "https://www.twitch.tv") return undefined;
  const login = /^\/([a-z0-9_]{1,25})\/?$/i.exec(url.pathname)?.[1].toLowerCase();
  return login && !nonChannelRoutes.has(login) ? login : undefined;
}

async function channelTabs() {
  const tabs = await chrome.tabs.query({ url: "https://www.twitch.tv/*" });
  return tabs.flatMap((tab) => {
    const login = channelLogin(tab);
    return login ? [{ tab, login }] : [];
  });
}

async function readWatchTabs() {
  const channels = await channelTabs();
  const logins = [...new Set(channels.map(({ login }) => login))].sort().slice(0, 500);
  const activeLogins = [...new Set(channels.filter(({ tab }) => tab.active).map(({ login }) => login))]
    .filter((login) => logins.includes(login));
  return { status: "complete", logins, activeLogins };
}

async function openChannel(login) {
  if (typeof login !== "string" || !channelLoginPattern.test(login) || nonChannelRoutes.has(login.toLowerCase())) {
    return { status: "unavailable", logins: [] };
  }
  const channels = await channelTabs();
  const existing = channels.find((channel) => channel.login === login.toLowerCase());
  const url = "https://www.twitch.tv/" + login;
  if (existing) {
    await chrome.tabs.update(existing.tab.id, { active: true });
    await chrome.windows.update(existing.tab.windowId, { focused: true });
  } else {
    const normalWindows = await chrome.windows.getAll({ windowTypes: ["normal"] });
    const tabWindows = new Set(channels.map(({ tab }) => tab.windowId));
    const eligible = normalWindows.filter((window) => tabWindows.has(window.id));
    const target = eligible.find((window) => window.focused) ?? eligible[0];
    if (target) {
      await chrome.tabs.create({ windowId: target.id, url, active: true });
      await chrome.windows.update(target.id, { focused: true });
    } else {
      await chrome.windows.create({ url, focused: true });
    }
  }
  scheduleTabChange();
  return { status: "complete", logins: [] };
}

const managedTabKey = "oshiManagedTabs";
const identifier = /^[a-f0-9]{32}$/;
const maxManagedTabs = 50;
let managedCommands = Promise.resolve();

async function readManagedTabs(scope, channels) {
  const { [managedTabKey]: stored = {} } = await chrome.storage.session.get(managedTabKey);
  const tabs = Object.entries(stored).flatMap(([tabId, mark]) => {
    const current = channels.find(({ tab }) => tab.id === Number(tabId));
    return current?.login === mark.login && mark.scope === scope
      ? [{ tabId: Number(tabId), login: mark.login, revision: mark.revision, channel: mark.channel }] : [];
  });
  const confirmed = new Set(tabs.map(tab => tab.tabId));
  const pending = Object.entries(stored).filter(([id, mark]) => mark.scope === scope &&
    mark.pendingUntil > Date.now() && !confirmed.has(Number(id))).length;
  return { stored, tabs, pending };
}

async function managedCommand(request) {
  if (!/^[a-f0-9]{64}$/.test(request.scope)) return { status: "unavailable", logins: [] };
  const channels = await channelTabs();
  const { stored, tabs, pending } = await readManagedTabs(request.scope, channels);
  const logins = [...new Set(channels.map(({ login }) => login))];
  const snapshot = () => logins.length <= 500 && tabs.length <= maxManagedTabs
    ? { status: "complete", logins, managedTabs: tabs }
    : { status: "partial", logins: [] };
  if (request.method === "oshi_tabs") return snapshot();

  const { action, tabId, revision, expectedLogin, assignment, channel } = request.mutation ?? {};
  const login = request.login?.toLowerCase();
  if (!["open", "switch", "close", "watch", "adopt"].includes(action) || !identifier.test(assignment))
    return { status: "unavailable", logins: [] };
  if (action !== "close" && (!channelLoginPattern.test(login ?? "") || nonChannelRoutes.has(login) ||
      channel?.provider !== "twitch" || typeof channel.id !== "string" || !/^\d{1,32}$/.test(channel.id)))
    return { status: "unavailable", logins: [] };

  if (tabs.some(tab => tab.revision === assignment))
    return { status: "complete", logins: [] };
  const existing = channels.filter(candidate => candidate.login === login);
  if ((action === "open" || action === "switch") && existing.length)
    return { status: "partial", logins: [] };

  if (action === "open" || action === "watch" || action === "adopt") {
    const cap = request.mutation.tabCap ?? maxManagedTabs;
    if (!Number.isInteger(cap) || cap < 0 || cap > maxManagedTabs)
      return { status: "unavailable", logins: [] };
    const reject = reason => ({ status: "partial", logins: [], reason });
    const owned = existing.find(({ tab }) => tabs.some(mark => mark.tabId === tab.id));
    if (owned) {
      if (action === "watch") {
        await chrome.tabs.update(owned.tab.id, { active: true });
        await chrome.windows.update(owned.tab.windowId, { focused: true });
      }
      return { status: "complete", logins: [] };
    }
    if (action === "adopt" && existing.length === 0) return reject("missing");
    // Count in-flight opens even before Chromium exposes their new Twitch URLs.
    if (tabs.length + pending >= cap) return reject("capacity");
    if (existing.length) {
      if (existing.length > 1) return reject("duplicate");
      const { tab } = existing[0];
      if (stored[tab.id]?.scope && stored[tab.id].scope !== request.scope) return reject("owned_elsewhere");
      if (channels.some(candidate => candidate.tab.id !== tab.id && candidate.tab.windowId === tab.windowId))
        return reject("shared_window");
      const current = await chrome.tabs.get(tab.id);
      if (channelLogin(current) !== login || channelLogin({ url: current.url }) !== login) return reject("missing");
      stored[tab.id] = { scope: request.scope, login, revision: assignment, channel };
      await chrome.storage.session.set({ [managedTabKey]: stored });
      await chrome.tabs.update(tab.id, { active: true });
      if (action === "watch") await chrome.windows.update(tab.windowId, { focused: true });
      scheduleTabChange();
      return { status: "complete", logins: [] };
    }

    const windows = await chrome.windows.getAll({ windowTypes: ["normal"] });
    const url = "https://www.twitch.tv/" + login;
    const focus = action === "watch" || request.mutation.focusOnOpen === true || windows.length === 0;
    const window = await chrome.windows.create({ url, focused: focus });
    const tab = window.tabs?.[0] ?? (await chrome.tabs.query({ windowId: window.id, active: true }))[0];
    if (!Number.isInteger(tab?.id)) return { status: "unavailable", logins: [] };
    stored[tab.id] = { scope: request.scope, login, revision: assignment, channel, pendingUntil: Date.now() + 5000 };
  } else {
    if (!tabs.some(tab => tab.tabId === tabId && tab.revision === revision && tab.login === expectedLogin))
      return { status: "partial", logins: [] };
    const current = await chrome.tabs.get(tabId);
    if (channelLogin(current) !== expectedLogin || channelLogin({ url: current.url }) !== expectedLogin)
      return { status: "partial", logins: [] };
    if (action === "close") {
      await chrome.tabs.remove(tabId);
      delete stored[tabId];
    } else {
      await chrome.tabs.update(tabId, { url: "https://www.twitch.tv/" + login });
      stored[tabId] = { scope: request.scope, login, revision: assignment, channel };
    }
  }
  await chrome.storage.session.set({ [managedTabKey]: stored });
  scheduleTabChange();
  return { status: "complete", logins: [] };
}

function queueManaged(operation) {
  const next = managedCommands.then(operation);
  managedCommands = next.catch(() => {});
  return next;
}

function releaseManaged(tabId, url, removed = false) {
  void queueManaged(async () => {
    const { [managedTabKey]: stored = {} } = await chrome.storage.session.get(managedTabKey);
    const mark = stored[tabId];
    if (!mark || (url && channelLogin({ url }) === mark.login)) return;
    if (!removed && mark.pendingUntil > Date.now() && (!url || url === "about:blank")) return;
    delete stored[tabId];
    await chrome.storage.session.set({ [managedTabKey]: stored });
  }).catch(() => {});
}

function scheduleTabChange() {
  clearTimeout(tabChange);
  tabChange = setTimeout(async () => {
    const port = connection;
    if (!port) return;
    try {
      const tabs = await readWatchTabs();
      const signature = JSON.stringify([tabs.logins, tabs.activeLogins.sort()]);
      if (connection !== port || signature === lastWatchSignature) return;
      port.postMessage({ event: "watch_changed", managed: true });
      lastWatchSignature = signature;
    } catch {
      // The desktop periodically checks the bridge in case an event arrives during disconnect.
    }
  }, 180);
}

chrome.tabs.onUpdated.addListener((id, change, tab) => {
  if (change.url || change.status === "complete") releaseManaged(id, change.url ?? tab.url);
  // Navigation away can omit the new URL when it leaves the permitted host.
  if (change.url || change.status === "complete" || tab.url?.startsWith("https://www.twitch.tv/")) scheduleTabChange();
});
chrome.tabs.onCreated.addListener(scheduleTabChange);
chrome.tabs.onRemoved.addListener(id => { releaseManaged(id, undefined, true); scheduleTabChange(); });
chrome.tabs.onReplaced.addListener((_, oldId) => { releaseManaged(oldId, undefined, true); scheduleTabChange(); });
chrome.tabs.onActivated.addListener(scheduleTabChange);

function connect() {
  if (!host || connection) return;
  const port = chrome.runtime.connectNative(host);
  connection = port;
  lastWatchSignature = undefined;
  port.onDisconnect.addListener(() => {
    // Reading lastError suppresses a warning when the app isn't paired yet.
    void chrome.runtime.lastError;
    if (connection === port) connection = undefined;
  });
  port.onMessage.addListener(async (request) => {
    if (request?.version !== 1 || !["gift_channels", "watch_channels", "open_watch_channel", "oshi_tabs", "oshi_mutate"].includes(request.method) ||
        typeof request.id !== "string" || !/^[a-f0-9]{32}$/.test(request.id)) return;
    let result;
    try {
      if (request.method === "gift_channels") result = await readGiftTabs();
      else if (request.method === "watch_channels") result = await readWatchTabs();
      else if (request.method === "open_watch_channel") result = await queueManaged(() => openChannel(request.login));
      else result = await queueManaged(() => managedCommand(request));
    } catch {
      result = { status: "unavailable", logins: [] };
    }
    if (connection === port) {
      port.postMessage({ version: 1, id: request.id, ...result });
    }
  });
  sendWakeHeartbeat();
  scheduleTabChange();
}

function sendWakeHeartbeat() {
  const port = connection;
  if (!port) return;
  try {
    port.postMessage({ event: "watch_changed", managed: true, wake: true });
  } catch {
    // A browser shutdown can disconnect between the alarm and postMessage.
    if (connection === port) connection = undefined;
  }
}

async function readGiftTabs() {
  const tabs = await chrome.tabs.query({ url: "https://www.twitch.tv/subscriptions*" });
  const gifts = tabs.filter((tab) => {
    const url = new URL(tab.url);
    return url.pathname === "/subscriptions" && url.searchParams.get("tab") === "gifts";
  });
  const results = await Promise.all(gifts.map(async (tab) => {
    try {
      const [injection] = await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        func: readGiftPage,
      });
      return injection?.result;
    } catch {
      return undefined;
    }
  }));
  const logins = [...new Set(results.flatMap((result) => result?.logins ?? []))];
  const statuses = results.map((result) => result?.status);
  const status = statuses.includes("complete") ? "complete"
    : statuses.includes("partial") ? "partial"
    : statuses.includes("signed_out") ? "signed_out" : "unavailable";
  return { status: logins.length > 500 ? "partial" : status, logins: logins.slice(0, 500) };
}

function readGiftPage() {
  const page = new URL(location.href);
  if (page.origin !== "https://www.twitch.tv" || page.pathname !== "/subscriptions" ||
      page.searchParams.get("tab") !== "gifts") {
    return { status: "unavailable", logins: [] };
  }
  const main = document.querySelector("main, [role=main]");
  if (!main) return { status: "unavailable", logins: [] };
  if (main.querySelector("form input[type=password]")) {
    return { status: "signed_out", logins: [] };
  }

  // Limit collection to the content panel and exclude surrounding site navigation.
  const panel = main.querySelector("[role=tabpanel]:not([hidden])") ?? main;
  const logins = [...new Set([...panel.querySelectorAll("a[href]")]
    .filter((anchor) => anchor.getClientRects().length && !anchor.closest("nav, header, footer, aside"))
    .flatMap((anchor) => {
      const url = new URL(anchor.href);
      const match = /^\/([a-z0-9_]{1,25})\/?$/i.exec(url.pathname);
      return url.origin === page.origin && match ? [match[1].toLowerCase()] : [];
    }))];

  // Only Helix can confirm candidates. Treat the rendered set as partial unless the
  // page itself gives a reliable signal that no further results exist.
  return { status: logins.length ? "partial" : "unavailable", logins };
}

chrome.alarms.onAlarm.addListener(({ name }) => {
  if (name !== "connect-oshituner") return;
  if (connection) sendWakeHeartbeat();
  else connect();
});
chrome.runtime.onStartup.addListener(start);
chrome.runtime.onInstalled.addListener(start);

function start() {
  if (!host) return;
  chrome.alarms.create("connect-oshituner", { periodInMinutes: 1 });
  connect();
}

start();

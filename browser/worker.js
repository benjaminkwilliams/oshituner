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
  if (!tab.url) return undefined;
  const url = new URL(tab.url);
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

function scheduleTabChange() {
  clearTimeout(tabChange);
  tabChange = setTimeout(async () => {
    const port = connection;
    if (!port) return;
    try {
      const tabs = await readWatchTabs();
      const signature = JSON.stringify([tabs.logins, tabs.activeLogins.sort()]);
      if (connection !== port || signature === lastWatchSignature) return;
      port.postMessage({ event: "watch_changed" });
      lastWatchSignature = signature;
    } catch {
      // The desktop periodically checks the bridge in case an event arrives during disconnect.
    }
  }, 180);
}

chrome.tabs.onUpdated.addListener((_, change, tab) => {
  // Navigation away can omit the new URL when it leaves the permitted host.
  if (change.url || change.status === "complete" || tab.url?.startsWith("https://www.twitch.tv/")) scheduleTabChange();
});
chrome.tabs.onCreated.addListener(scheduleTabChange);
chrome.tabs.onRemoved.addListener(scheduleTabChange);
chrome.tabs.onReplaced.addListener(scheduleTabChange);
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
    if (request?.version !== 1 || !["gift_channels", "watch_channels", "open_watch_channel"].includes(request.method) ||
        typeof request.id !== "string" || !/^[a-f0-9]{32}$/.test(request.id)) return;
    let result;
    try {
      if (request.method === "gift_channels") result = await readGiftTabs();
      else if (request.method === "watch_channels") result = await readWatchTabs();
      else result = await openChannel(request.login);
    } catch {
      result = { status: "unavailable", logins: [] };
    }
    if (connection === port) {
      port.postMessage({ version: 1, id: request.id, ...result });
    }
  });
  scheduleTabChange();
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
  if (name === "connect-oshituner") connect();
});
chrome.runtime.onStartup.addListener(start);
chrome.runtime.onInstalled.addListener(start);

function start() {
  if (!host) return;
  chrome.alarms.create("connect-oshituner", { periodInMinutes: 1 });
  connect();
}

start();

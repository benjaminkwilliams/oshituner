const hosts = {
  cagcflabpbciehhnjchjipdipmgplipl: "com.oshituner.edge",
  hnomfkgbacjeiocjcgbnnoaaloleefjf: "com.oshituner.chrome",
};
const host = hosts[chrome.runtime.id];
let connection;

function connect() {
  if (!host || connection) return;
  const port = chrome.runtime.connectNative(host);
  connection = port;
  port.onDisconnect.addListener(() => {
    // Reading lastError suppresses a warning when the app isn't paired yet.
    void chrome.runtime.lastError;
    if (connection === port) connection = undefined;
  });
  port.onMessage.addListener(async (request) => {
    if (request?.version !== 1 || request.method !== "gift_channels" ||
        typeof request.id !== "string" || !/^[a-f0-9]{32}$/.test(request.id)) return;
    let result;
    try {
      result = await readGiftTabs();
    } catch {
      result = { status: "unavailable", logins: [] };
    }
    if (connection === port) {
      port.postMessage({ version: 1, id: request.id, ...result });
    }
  });
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

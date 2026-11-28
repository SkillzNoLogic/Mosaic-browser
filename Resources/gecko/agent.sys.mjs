/* Mosaic's agent inside Gecko.
 *
 * Mosaic loads this module into Gecko's parent process (through the remote
 * debugging server: a Unix socket on macOS, a loopback TCP port on Windows) and
 * calls the methods of `Agent` to drive the engine: open/close/select tabs,
 * navigate, make the Gecko window chromeless so Mosaic can show it in its own
 * content area, and manage converted Chrome extensions.
 *
 * Every method returns a JSON string (or a promise of one) so results cross the
 * debugging protocol as plain values.
 *
 * The same file serves every platform. Where they differ it is spelled out:
 * the shortcut modifier, the units of `setRect`, and how the window is made
 * borderless.
 */

const lazy = {};
ChromeUtils.defineESModuleGetters(lazy, {
  AddonManager: "resource://gre/modules/AddonManager.sys.mjs",
  ExtensionPermissions: "resource://gre/modules/ExtensionPermissions.sys.mjs",
});

const SYSTEM_PRINCIPAL = Services.scriptSecurityManager.getSystemPrincipal();

/** "Darwin" on macOS, "WINNT" on Windows, "Linux" on Linux. */
const IS_MAC = Services.appinfo.OS === "Darwin";

/** Stable numeric IDs for tabs; Mosaic refers to tabs by these. */
const tabIds = new WeakMap();
let nextTabId = 1;

/** Tabs Gecko opened by itself at startup; hidden from Mosaic and closed on first use. */
const startupTabs = new WeakSet();

/** Windows that already have Mosaic's hooks installed. */
const hookedWindows = new WeakSet();

/** Things that happened inside Gecko which Mosaic's UI must react to. */
const pendingEvents = [];

/**
 * Password events kept apart from pendingEvents when passwordsStart was asked to
 * (Windows, whose snapshot reader keeps only event types); read with passwordsTake().
 */
const passwordEvents = [];
const MAX_PASSWORD_EVENTS = 200;

/**
 * Converted extensions are re-installed every time Gecko starts, so each start
 * looks like a first install to them and many open their welcome page again.
 * While this deadline is in the future, tabs Mosaic did not ask for are closed
 * as soon as they appear.
 */
let quietUntil = 0;
let openingOwnTab = false;

function onTabOpen(event) {
  if (openingOwnTab || Date.now() >= quietUntil) {
    return;
  }
  const tab = event.target;
  const win = tab.ownerGlobal;
  startupTabs.add(tab); // never reported to Mosaic
  win.setTimeout(() => {
    if (tab.isConnected && win.gBrowser.tabs.length > 1) {
      win.gBrowser.removeTab(tab, { animate: false });
    }
  }, 0);
}

function browserWindow() {
  return Services.wm.getMostRecentWindow("navigator:browser");
}

// ---- Content blocker --------------------------------------------------------
//
// Mosaic's built-in blocker is uBlock Origin's network filtering engine, bundled
// from uBO's source (Resources/blocker/engine.js). The same bundle runs inside
// the Mosaic app for Chromium tabs; here it runs in a sandbox in Gecko's parent
// process, and every HTTP request a tab makes is put to it before it is sent.

const blocker = {
  /** The loaded engine (the sandbox's `MosaicBlocker`), or null. */
  engine: null,
  enabled: true,
  /** Normalised hostnames blocking is switched off for (subdomains included). */
  allowlist: new Set(),
  observing: false,
  /** Requests blocked since each tab's current page started loading. */
  blocked: new WeakMap(),
};

function isAllowlisted(hostname) {
  let host = hostname.startsWith("www.") ? hostname.slice(4) : hostname;
  for (;;) {
    if (blocker.allowlist.has(host)) {
      return true;
    }
    const dot = host.indexOf(".");
    if (dot === -1) {
      return false;
    }
    host = host.slice(dot + 1);
  }
}

/** `http-on-modify-request` observer: the last point a request can be stopped. */
function onHttpRequest(subject) {
  try {
    const channel = subject.QueryInterface(Ci.nsIHttpChannel);
    // Only requests that belong to a tab. Gecko's own traffic and extensions'
    // background requests have no tab and are left alone.
    const browser = channel.loadInfo?.browsingContext?.top?.embedderElement;
    // Looked up through the browser window itself: `gBrowser` is not reachable
    // from the element's `ownerGlobal` as seen from this module.
    const tab = browser ? browserWindow()?.gBrowser?.getTabForBrowser(browser) : null;
    if (!tab) {
      return;
    }
    const request = ChannelWrapper.get(channel);
    // Gecko reports <img srcset> loads as "imageset"; filters know them as images.
    const type = request.type === "imageset" ? "image" : request.type;
    if (type === "main_frame") {
      // A page's own document is never blocked; restart its count. This runs
      // even while blocking is off, so a stale count does not linger.
      blocker.blocked.set(tab, 0);
      privacyResetBlocked(tab); // privacy area: blocker dashboard
      return;
    }
    if (!blocker.enabled || blocker.engine === null) {
      return;
    }
    const page = browser.currentURI;
    if (!page || !page.schemeIs("http") && !page.schemeIs("https")) {
      return;
    }
    if (isAllowlisted(page.host)) {
      return;
    }
    if (blocker.engine.match(request.finalURL, page.spec, type) !== 1) {
      return;
    }
    channel.cancel(Cr.NS_ERROR_ABORT);
    blocker.blocked.set(tab, (blocker.blocked.get(tab) ?? 0) + 1);
    privacyNoteBlocked(tab, request.finalURL, type, page.spec); // privacy area: blocker dashboard
  } catch (error) {
    // A request the blocker cannot classify is let through.
    console.error("Mosaic blocker:", error);
  }
}

/** Gives the main thread a turn, so loading filter lists does not freeze Gecko. */
function nextTick() {
  return new Promise(resolve => Services.tm.dispatchToMainThread(resolve));
}

function requireWindow() {
  const win = browserWindow();
  if (!win || !win.gBrowser) {
    throw new Error("Gecko has no browser window");
  }
  return win;
}

function idOf(tab) {
  let id = tabIds.get(tab);
  if (!id) {
    id = nextTabId++;
    tabIds.set(tab, id);
  }
  return id;
}

function findTab(win, id) {
  return win.gBrowser.tabs.find(tab => tabIds.get(tab) === id) ?? null;
}

function requireTab(id) {
  const win = requireWindow();
  const tab = findTab(win, id);
  if (!tab) {
    throw new Error(`no tab ${id}`);
  }
  return { win, tab, browser: tab.linkedBrowser };
}

function load(browser, url) {
  const options = { triggeringPrincipal: SYSTEM_PRINCIPAL };
  if (typeof browser.fixupAndLoadURIString === "function") {
    browser.fixupAndLoadURIString(url, options);
  } else {
    browser.loadURI(Services.io.newURI(url), options);
  }
}

function baseWindow(win) {
  return win.docShell.treeOwner.QueryInterface(Ci.nsIBaseWindow);
}

/**
 * The window's native handle (the HWND on Windows) as a decimal string; "0" if
 * it has none, which is the case in headless mode. Gecko itself reports the
 * handle as "0x…" text because a pointer does not fit a JS number, so it is
 * kept as a string on its way to Mosaic too.
 */
function nativeHandle(win) {
  try {
    return BigInt(baseWindow(win).nativeHandle).toString();
  } catch (_) {
    return "0";
  }
}

/**
 * True when the platform's shortcut modifier (Command on macOS, Control
 * everywhere else) is held, alone or with Shift. Control+Alt is excluded: on
 * Windows that is how AltGr arrives, and it types characters.
 */
function hasShortcutModifier(event) {
  if (event.altKey) {
    return false;
  }
  return IS_MAC ? event.metaKey && !event.ctrlKey : event.ctrlKey && !event.metaKey;
}

/** Shortcuts that belong to Mosaic's UI even while Gecko has focus. */
const FORWARDED = { l: "focusAddress", t: "newTab", n: "newTab", w: "closeTab" };
const FORWARDED_WITH_SHIFT = { e: "toggleEngine", b: "toggleBookmarksBar" };
// Engine area: Mosaic's find bar replaces Firefox's (macOS app only).
if (IS_MAC) {
  Object.assign(FORWARDED, { f: "find", g: "findNext" });
  Object.assign(FORWARDED_WITH_SHIFT, { g: "findPrevious" });
}
// Library area (Windows app only): Ctrl+D bookmarks the page, Ctrl+H shows Mosaic's history
// and Ctrl+Shift+O its bookmarks. Firefox's own would use its hidden bookmarks, its sidebar or
// a separate Library window. (On macOS the menu bar takes these keys before Gecko sees them.)
if (Services.appinfo.OS === "WINNT") {
  Object.assign(FORWARDED, { d: "bookmarkPage", h: "showHistory" });
  Object.assign(FORWARDED_WITH_SHIFT, { o: "showBookmarks" });
}

/**
 * Gecko's own shortcuts for whole windows: close window, reopen closed window,
 * new private window, and (where it is a key binding rather than a system menu
 * item) quit. Mosaic shows exactly one Gecko window, as part of its own, so
 * these would close the engine under it or put a second, chromeless Gecko
 * window on the desktop. They are swallowed.
 */
const SWALLOWED_WITH_SHIFT = new Set(IS_MAC ? ["w", "n", "p"] : ["w", "n", "p", "q"]);

function onKeyDown(event) {
  // productivity: shortcuts the user assigned in Mosaic (see forwardCustomShortcut).
  if (forwardCustomShortcut(event)) {
    return;
  }
  if (!hasShortcutModifier(event)) {
    return;
  }
  const key = event.key.toLowerCase();
  const forwarded = event.shiftKey ? FORWARDED_WITH_SHIFT : FORWARDED;
  const type = Object.hasOwn(forwarded, key) ? forwarded[key] : null;
  if (!type && !(event.shiftKey && SWALLOWED_WITH_SHIFT.has(key))) {
    return;
  }
  event.preventDefault();
  event.stopImmediatePropagation();
  if (type) {
    pendingEvents.push({ type });
  }
}

function describeAddon(addon) {
  return {
    id: addon.id,
    name: addon.name,
    version: addon.version,
    active: addon.isActive,
    temporary: !!addon.temporarilyInstalled,
  };
}

function extensionPage(id, kind) {
  const policy = WebExtensionPolicy.getByID(id);
  const manifest = policy?.extension?.manifest;
  if (!manifest) {
    return null;
  }
  let path = null;
  if (kind === "popup") {
    path = manifest.action?.default_popup ?? manifest.browser_action?.default_popup ?? null;
  } else if (kind === "options") {
    path = manifest.options_ui?.page ?? null;
  }
  if (!path) {
    return null;
  }
  return /^[a-z-]+:/.test(path) ? path : policy.getURL(path.replace(/^\//, ""));
}

// ---- Downloads (downloads area) ---------------------------------------------
//
// Mosaic lists and controls Gecko's downloads in its own UI. The agent watches Gecko's
// download list (Downloads.ALL) and Mosaic polls `downloadsState(version)`, which answers
// with a one-line "unchanged" unless a download changed since that version. Nothing here
// runs until Mosaic calls `configureDownloads`, so existing behaviour is untouched.

const downloadsLazy = {};
ChromeUtils.defineESModuleGetters(downloadsLazy, {
  Downloads: "resource://gre/modules/Downloads.sys.mjs",
});

const downloads = {
  /** The DownloadList being watched, once `watchDownloads` has finished. */
  list: null,
  listPromise: null,
  /** Stable numeric IDs; Mosaic refers to downloads by these. */
  ids: new WeakMap(),
  byId: new Map(),
  nextId: 1,
  /** Bumped whenever anything Mosaic shows about a download changes. */
  version: 0,
  /** Downloads Mosaic paused (Gecko pauses by cancelling and keeping partial data). */
  paused: new WeakSet(),
  /** Downloads already in Gecko's list when the agent started watching. */
  restored: new WeakSet(),
  /** Requests from inside Gecko for Mosaic: a shortcut pressed, a context-menu item chosen. */
  commands: [],
  /** Whether the context menu offers "Download Linked File with Free Download Manager". */
  fdmMenu: false,
  hookedWindows: new WeakSet(),
};

function trackDownload(download, restored) {
  if (!downloads.ids.has(download)) {
    const id = downloads.nextId++;
    downloads.ids.set(download, id);
    downloads.byId.set(id, download);
    if (restored) {
      downloads.restored.add(download);
    }
  }
  downloads.version++;
}

function untrackDownload(download) {
  const id = downloads.ids.get(download);
  if (id !== undefined) {
    downloads.byId.delete(id);
  }
  downloads.paused.delete(download);
  downloads.version++;
}

async function watchDownloads() {
  if (!downloads.listPromise) {
    downloads.listPromise = (async () => {
      const list = await downloadsLazy.Downloads.getList(downloadsLazy.Downloads.ALL);
      let initial = true;
      // addView reports the downloads already in the list synchronously.
      list.addView({
        onDownloadAdded: download => trackDownload(download, initial),
        onDownloadChanged: download => trackDownload(download, false),
        onDownloadRemoved: download => untrackDownload(download),
      });
      initial = false;
      downloads.list = list;
    })();
  }
  await downloads.listPromise;
}

function describeDownloadError(error) {
  if (!error) {
    return "";
  }
  if (error.becauseBlockedByReputationCheck) {
    return "Gecko blocked this file as potentially harmful";
  }
  if (error.becauseBlockedByParentalControls) {
    return "Blocked by parental controls";
  }
  if (error.becauseBlocked) {
    return "Gecko blocked this download";
  }
  if (error.becauseTargetFailed) {
    return "Could not write the file";
  }
  if (error.becauseSourceFailed) {
    return "Network error";
  }
  return String(error.message || "Download failed");
}

function describeDownload(download) {
  let state;
  if (!download.stopped) {
    state = "inProgress";
  } else if (download.succeeded) {
    state = "completed";
  } else if (download.error) {
    state = "failed";
  } else if (downloads.paused.has(download)) {
    state = "paused";
  } else if (download.canceled) {
    // Gecko's own pause: cancelled with the partial data kept.
    state = download.hasPartialData ? "paused" : "cancelled";
  } else {
    state = "inProgress"; // created, not started yet
  }
  let referrer = "";
  try {
    referrer = download.source.referrerInfo?.originalReferrer?.spec ?? "";
  } catch (_) {}
  return {
    id: downloads.ids.get(download),
    url: download.source.url,
    referrer,
    path: download.target.path ?? "",
    contentType: download.contentType ?? "",
    totalBytes: download.hasProgress ? download.totalBytes : -1,
    currentBytes: download.currentBytes,
    speed: Math.round(download.speed || 0),
    state,
    error: describeDownloadError(download.error),
    hasPartialData: !!download.hasPartialData,
    restored: downloads.restored.has(download),
    private: !!download.source.isPrivate,
    startTime: download.startTime ? download.startTime.getTime() : 0,
  };
}

/** Shortcuts for Mosaic's downloads page: ⌥⌘L / Ctrl+J, and Gecko's own ⌘⇧Y / Ctrl+Shift+Y. */
function onDownloadsKeyDown(event) {
  const code = event.code;
  const mosaicShortcut = IS_MAC
    ? event.metaKey && event.altKey && !event.ctrlKey && !event.shiftKey && code === "KeyL"
    : event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey && code === "KeyJ";
  // Gecko would open its own Library window, outside Mosaic.
  const libraryShortcut = (IS_MAC ? event.metaKey && !event.ctrlKey : event.ctrlKey && !event.metaKey) &&
    event.shiftKey && !event.altKey && code === "KeyY";
  if (!mosaicShortcut && !libraryShortcut) {
    return;
  }
  event.preventDefault();
  event.stopImmediatePropagation();
  downloads.commands.push({ type: "showDownloads" });
}

function hookDownloadsWindow(win) {
  if (!win || !win.document || downloads.hookedWindows.has(win)) {
    return;
  }
  downloads.hookedWindows.add(win);
  win.addEventListener("keydown", onDownloadsKeyDown, true);

  const menu = win.document.getElementById("contentAreaContextMenu");
  if (!menu) {
    return;
  }
  const item = win.document.createXULElement("menuitem");
  item.id = "mosaic-download-with-fdm";
  item.setAttribute("label", "Download Linked File with Free Download Manager");
  item.hidden = true;
  const anchor = win.document.getElementById("context-savelink");
  if (anchor) {
    anchor.after(item);
  } else {
    menu.appendChild(item);
  }
  menu.addEventListener("popupshowing", event => {
    if (event.target !== menu) {
      return;
    }
    const context = win.gContextMenu;
    item.hidden = !(downloads.fdmMenu && context?.onLink && /^(https?|ftp):/i.test(context.linkURL ?? ""));
  });
  item.addEventListener("command", () => {
    const context = win.gContextMenu;
    if (!context?.linkURL) {
      return;
    }
    let page = "";
    try {
      page = context.browser?.currentURI?.spec ?? "";
    } catch (_) {}
    downloads.commands.push({ type: "sendLinkToFDM", url: context.linkURL, referrer: page });
  });
}

/** The Cookie header Gecko would send to `url` (first-party, unpartitioned cookies). */
function cookieHeaderFor(url) {
  const uri = Services.io.newURI(url);
  const host = uri.host;
  const path = uri.filePath || "/";
  const secure = uri.schemeIs("https");
  const pairs = [];
  for (const cookie of Services.cookies.getCookiesFromHost(host, {})) {
    const domain = cookie.host;
    const domainMatches = domain.startsWith(".")
      ? host === domain.slice(1) || host.endsWith(domain)
      : host === domain;
    if (!domainMatches || (cookie.isSecure && !secure) || !path.startsWith(cookie.path || "/")) {
      continue;
    }
    pairs.push(`${cookie.name}=${cookie.value}`);
  }
  return pairs.join("; ");
}

// ---- Passwords (area: passwords) --------------------------------------------
//
// Save and fill live in passwords.sys.mjs and the MosaicPasswords window actors. The agent
// only hands them its event queue and its tab IDs. Loaded on first use, so a build without
// those files is unaffected.

function passwords() {
  return ChromeUtils.importESModule("resource://mosaic/passwords.sys.mjs").MosaicPasswords;
}
// ---- Customised shortcuts and reader view (productivity) --------------------
//
// Mosaic lets the user change its keyboard shortcuts. Shortcuts pressed while a Gecko
// page has focus never reach Mosaic's menus, so Mosaic sends the current list here and
// matching key presses are queued as events ("productivity.shortcut:<command>").

/** Chord ("accel+shift+k") → event type. Empty until Mosaic sends a list. */
const customShortcuts = new Map();

/** Characters that already imply Shift; Mosaic stores such chords without "shift". */
const SHIFTED_SYMBOLS = new Set([..."~!@#$%^&*()_+{}|:\"<>?"]);

/**
 * The chord of a key press, in the form Mosaic sends: modifiers in the order accel
 * (Command on macOS, Control elsewhere), ctrl (macOS only), alt, shift, then the key as
 * KeyboardEvent.key (letters lower-case).
 */
function chordOf(event) {
  let key = event.key;
  if (event.altKey) {
    // Option changes the character on macOS (Option-K types "˚"): use the physical key.
    const match = /^(?:Key([A-Z])|Digit([0-9]))$/.exec(event.code);
    if (match) {
      key = match[1] ?? match[2];
    }
  }
  if (key.length === 1) {
    key = key.toLowerCase();
  }
  const shift = event.shiftKey && !(key.length === 1 && SHIFTED_SYMBOLS.has(key));
  const parts = [];
  if (IS_MAC ? event.metaKey : event.ctrlKey) {
    parts.push("accel");
  }
  if (IS_MAC && event.ctrlKey) {
    parts.push("ctrl");
  }
  if (event.altKey) {
    parts.push("alt");
  }
  if (shift) {
    parts.push("shift");
  }
  parts.push(key);
  return parts.join("+");
}

function forwardCustomShortcut(event) {
  if (customShortcuts.size === 0 || ["Meta", "Control", "Alt", "Shift"].includes(event.key)) {
    return false;
  }
  const type = customShortcuts.get(chordOf(event));
  if (!type) {
    return false;
  }
  event.preventDefault();
  event.stopImmediatePropagation();
  pendingEvents.push({ type });
  return true;
}

/** The address an about:reader page shows, or null for any other page. */
function readerOriginalURL(spec) {
  if (!spec.startsWith("about:reader?")) {
    return null;
  }
  return new URLSearchParams(spec.slice("about:reader?".length)).get("url");
}

/** Preferences of Gecko's reader view that Mosaic keeps in step with its own settings. */
const READER_PREFS = {
  "reader.font_type": "char",
  "reader.font_size": "int",
  "reader.content_width": "int",
  "reader.line_height": "int",
  "reader.color_scheme": "char",
};

// ---- Page features (engine area) --------------------------------------------
//
// Find in page, zoom, printing, developer tools, mute, screenshots, permission
// requests, blocked pop-ups and page full screen. Mosaic draws the UI for all
// of them, so the Firefox UI they would normally use (find bar, permission
// panels anchored to the hidden toolbar, the pop-up notification bar) is
// replaced by events Mosaic picks up from `state()`. Nothing here runs until
// Mosaic calls `configurePageFeatures`.

const page = {
  configured: false,
  /** Browsers whose finder has Mosaic's listener; browser → listener. */
  finders: new WeakMap(),
  /** Browsers with an open find session; browser → { caseSensitive }. */
  findSessions: new WeakMap(),
  /** Pending permission requests Mosaic is asked about; id → request record. */
  requests: new Map(),
  nextRequestId: 1,
};

const { setTimeout, clearTimeout } = ChromeUtils.importESModule("resource://gre/modules/Timer.sys.mjs");

/** Queues an event for Mosaic with a payload (`detail`, a JSON string). */
function postPageEvent(type, tab, detail) {
  pendingEvents.push({ type, tab: tab ? idOf(tab) : null, detail: JSON.stringify(detail ?? {}) });
}


function tabForBrowser(browser) {
  return browser ? browserWindow()?.gBrowser?.getTabForBrowser(browser) ?? null : null;
}

/** Mosaic's permission names → the keys Gecko's permission manager uses. */
const GECKO_PERMISSION_KEYS = { location: "geo", notifications: "desktop-notification", popups: "popup" };

/** Content-permission request types Mosaic asks about, and what it calls them. */
const CONTENT_PERMISSION_KINDS = {
  geolocation: "location",
  "desktop-notification": "notifications",
  "persistent-storage": "other",
  "storage-access": "other",
  midi: "other",
  xr: "other",
  "local-network": "other",
  "loopback-network": "other",
  serial: "other",
};

function askMosaic(record, tab, detail) {
  const id = page.nextRequestId++;
  page.requests.set(id, record);
  postPageEvent("permissionRequest", tab, { id, ...detail });
  return id;
}

/** `Integration.contentPermission` override: geolocation, notifications and the rest. */
function contentPermissionOverride(base) {
  return {
    createPermissionPrompt(type, request) {
      const kind = CONTENT_PERMISSION_KINDS[type];
      const tab = kind && page.configured ? tabForBrowser(request.element) : null;
      if (!tab) {
        return base.createPermissionPrompt.call(this, type, request);
      }
      return {
        prompt() {
          askMosaic({ type: "content", request }, tab, {
            origin: request.principal.origin,
            kinds: [kind],
            other: kind === "other" ? [type] : [],
          });
        },
      };
    },
  };
}

/** Takes over camera / microphone requests (screen sharing stays with Firefox). */
function interceptWebRTC(actor, data) {
  if (!data.secure || data.sharingScreen || data.audioOutputDevices?.length) {
    return false;
  }
  const camera = data.videoInputDevices?.[0];
  const microphone = data.audioInputDevices?.[0];
  const tab = tabForBrowser(actor.getBrowser());
  if ((!camera && !microphone) || !tab) {
    return false;
  }
  const kinds = [];
  if (camera) kinds.push("camera");
  if (microphone) kinds.push("microphone");
  // Firefox's own handler fills in data.origin; this runs before it.
  const origin = actor.manager.topWindowContext.documentPrincipal.origin;
  askMosaic({ type: "webrtc", actor, data, camera, microphone }, tab, { origin, kinds, other: [] });
  return true;
}

function answerWebRTC(record, allow) {
  const { actor, data, camera, microphone } = record;
  if (!allow) {
    actor.denyRequest(data);
    return;
  }
  const principal = actor.manager.topWindowContext.documentPrincipal;
  const devices = [];
  if (camera) {
    // As Firefox's own prompt does when camera access is granted.
    Services.perms.addFromPrincipal(principal, "MediaManagerVideo", Services.perms.ALLOW_ACTION,
      Services.perms.EXPIRE_SESSION);
    devices.push(camera.deviceIndex);
    actor.activateDevicePerm(data.windowID, camera.mediaSource, camera.rawId);
  }
  if (microphone) {
    devices.push(microphone.deviceIndex);
    actor.activateDevicePerm(data.windowID, microphone.mediaSource, microphone.rawId);
  }
  actor.checkOSPermission(!!camera, !!microphone, false).then(granted => {
    if (granted) {
      actor.sendAsyncMessage("webrtc:Allow", { callID: data.callID, windowID: data.windowID, devices });
    } else {
      actor.denyRequestNoPermission(data);
    }
  });
}

let webRTCPatched = false;

function patchWebRTC() {
  if (webRTCPatched) {
    return;
  }
  const { WebRTCParent } = ChromeUtils.importESModule("resource:///actors/WebRTCParent.sys.mjs");
  const original = WebRTCParent.prototype.receiveMessage;
  WebRTCParent.prototype.receiveMessage = function (message) {
    try {
      if (page.configured && message.name === "webrtc:Request" && interceptWebRTC(this, message.data)) {
        return undefined;
      }
      if (page.configured && message.name === "webrtc:CancelRequest") {
        for (const [id, record] of page.requests) {
          if (record.type === "webrtc" && record.data.callID === message.data) {
            page.requests.delete(id);
            postPageEvent("permissionCancel", null, { id });
          }
        }
      }
    } catch (error) {
      console.error("Mosaic: camera/microphone request", error);
    }
    return original.call(this, message);
  };
  webRTCPatched = true;
}

/** Pop-ups Gecko blocked: reported to Mosaic instead of Firefox's notification bar. */
function onBlockedPopups(event) {
  const browser = event.target;
  const tab = tabForBrowser(browser);
  const blocker = browser?.popupAndRedirectBlocker;
  if (!tab || !blocker) {
    return;
  }
  event.stopImmediatePropagation();
  const count = blocker.getBlockedPopupCount();
  if (!count) {
    return;
  }
  blocker.getBlockedPopups().then(
    popups => postPageEvent("popupBlocked", tab, { count, urls: popups.map(popup => popup.popupWindowURISpec) }),
    () => postPageEvent("popupBlocked", tab, { count, urls: [] })
  );
}

/** Reports the page entering and leaving full screen (the JavaScript API). */
function watchFullscreen(win) {
  let wasFullscreen = false;
  const observer = new win.MutationObserver(() => {
    const now = win.document.documentElement.hasAttribute("inDOMFullscreen");
    if (now !== wasFullscreen) {
      wasFullscreen = now;
      postPageEvent("pageFullscreen", win.gBrowser.selectedTab, { value: now });
    }
  });
  observer.observe(win.document.documentElement, { attributes: true, attributeFilter: ["inDOMFullscreen"] });
}

function findListenerFor(browser) {
  let listener = page.finders.get(browser);
  if (listener) {
    return listener;
  }
  listener = {
    waiter: null,
    onFindResult(data) {
      const waiter = this.waiter;
      if (!waiter || data.searchString !== waiter.text) {
        return;
      }
      waiter.result = data.result;
      if (data.result === Ci.nsITypeAheadFind.FIND_NOTFOUND) {
        waiter.finish({ found: false, wrapped: false, current: 0, total: 0, limit: 0 });
      }
    },
    onMatchesCountResult(data) {
      const waiter = this.waiter;
      if (!waiter || data.searchString !== waiter.text || waiter.result === null) {
        return;
      }
      waiter.finish({
        found: waiter.result !== Ci.nsITypeAheadFind.FIND_NOTFOUND,
        wrapped: waiter.result === Ci.nsITypeAheadFind.FIND_WRAPPED,
        current: data.current,
        total: data.total,
        limit: data.limit,
      });
    },
    onHighlightFinished() {},
    onCurrentSelection() {},
  };
  browser.finder.addResultListener(listener);
  page.finders.set(browser, listener);
  return listener;
}

/** Draws part of a page (CSS pixels, document coordinates; null for the visible area). */
async function snapshotPNG(browser, rect, width, height, scale, path) {
  const wgp = browser.browsingContext.currentWindowGlobal;
  const canvas = new OffscreenCanvas(Math.round(width * scale), Math.round(height * scale));
  const context = canvas.getContext("2d");
  context.fillStyle = "rgb(255,255,255)";
  context.fillRect(0, 0, canvas.width, canvas.height);
  if (!rect) {
    const bitmap = await wgp.drawSnapshot(null, scale, "rgb(255,255,255)");
    context.drawImage(bitmap, 0, 0);
    bitmap.close();
  } else {
    // In bands, as Firefox's own screenshots do, to stay within texture limits.
    const band = 4096;
    for (let top = 0; top < height; top += band) {
      const h = Math.min(band, height - top);
      const bitmap = await wgp.drawSnapshot(new DOMRect(rect.x, rect.y + top, width, h), scale, "rgb(255,255,255)");
      context.drawImage(bitmap, 0, Math.floor(top * scale), Math.round(width * scale), Math.floor(h * scale));
      bitmap.close();
    }
  }
  const blob = await canvas.convertToBlob({ type: "image/png" });
  await IOUtils.write(path, new Uint8Array(await blob.arrayBuffer()));
  return { width: canvas.width, height: canvas.height };
}

export const Agent = {
  /**
   * Installs Mosaic's hooks in the browser window. Returns "false" until
   * Gecko's window exists and has finished starting up; call until "true".
   */
  setup() {
    const win = browserWindow();
    if (!win || !win.gBrowser || !win.gBrowserInit?.delayedStartupFinished) {
      return "false";
    }
    if (!hookedWindows.has(win)) {
      hookedWindows.add(win);
      for (const tab of win.gBrowser.tabs) {
        startupTabs.add(tab);
      }
      if (!IS_MAC && win.windowState !== win.STATE_NORMAL) {
        // A new profile can open maximized. Mosaic is about to size this
        // window itself, and Gecko lays a maximized window out differently
        // (it compensates for borders the system pushes off-screen).
        win.restore();
      }
      // Borderless: no title bar, no frame, no traffic lights. Mosaic's window
      // frames it. On Windows this has to happen now, while the window is
      // still top-level: once Mosaic has adopted it as a child window Gecko
      // declines to touch its styles ("embedded context" in
      // nsWindow::HideWindowChrome).
      win.document.documentElement.setAttribute("hidechrome", "true");
      win.addEventListener("keydown", onKeyDown, true);
      win.gBrowser.tabContainer.addEventListener("TabOpen", onTabOpen);
    }
    return "true";
  },

  /** Snapshot of every tab, plus any events queued since the last call. */
  state() {
    const win = browserWindow();
    if (!win || !win.gBrowser) {
      return JSON.stringify({ ready: false, tabs: [], events: [] });
    }
    const tabs = [];
    for (const tab of win.gBrowser.tabs) {
      const browser = tab.linkedBrowser;
      // A tab being torn down can briefly have no browser left.
      if (startupTabs.has(tab) || tab.closing || !browser) {
        continue;
      }
      let url = "";
      try {
        url = browser.currentURI?.spec ?? "";
        // On a brand-new profile the first location change of a tab can fail to
        // reach the parent process: the page is loaded but currentURI still
        // reads about:blank. The window global always knows the real document.
        if (url === "" || url === "about:blank") {
          const loaded = browser.browsingContext?.currentWindowGlobal?.documentURI?.spec;
          if (loaded && loaded !== "about:blank") {
            url = loaded;
          }
        }
      } catch (_) {}
      // Tabs area: a discarded tab (see discardTab) has no linked panel, and reading
      // canGoBack/canGoForward on its lazy browser would load it again.
      const discarded = !tab.linkedPanel;
      tabs.push({
        id: idOf(tab),
        url,
        title: tab.label ?? "",
        loading: tab.hasAttribute("busy"),
        canGoBack: !discarded && !!browser.canGoBack,
        canGoForward: !discarded && !!browser.canGoForward,
        selected: tab.selected,
        blocked: blocker.blocked.get(tab) ?? 0,
      });
    }
    return JSON.stringify({ ready: true, tabs, events: pendingEvents.splice(0) });
  },

  /**
   * Moves and sizes the Gecko window (screen coordinates, top-left origin).
   * The units are the ones each Mosaic works in: points on macOS, which are
   * Gecko's CSS pixels there, and physical pixels everywhere else.
   */
  setRect(x, y, width, height) {
    const win = requireWindow();
    if (win.windowState === win.STATE_MINIMIZED) {
      win.restore();
    }
    if (!IS_MAC) {
      // nsIBaseWindow works in device pixels, so no scale factor is involved
      // and the result is exact on displays scaled above 100%.
      baseWindow(win).setPositionAndSize(x, y, width, height, 0);
      return "true";
    }
    if (win.screenX !== x || win.screenY !== y) {
      win.moveTo(x, y);
    }
    if (win.outerWidth !== width || win.outerHeight !== height) {
      win.resizeTo(width, height);
    }
    return "true";
  },

  /** Where the Gecko window actually is; used to verify docking. */
  rect() {
    const win = requireWindow();
    return JSON.stringify({
      x: win.screenX,
      y: win.screenY,
      width: win.outerWidth,
      height: win.outerHeight,
      innerWidth: win.gBrowser.selectedBrowser.clientWidth,
      innerHeight: win.gBrowser.selectedBrowser.clientHeight,
      hidechrome: win.document.documentElement.getAttribute("hidechrome"),
    });
  },

  openTab(url, select) {
    const win = requireWindow();
    let tab;
    openingOwnTab = true;
    try {
      tab = win.gBrowser.addTab(url || "about:blank", {
        triggeringPrincipal: SYSTEM_PRINCIPAL,
      });
    } finally {
      openingOwnTab = false;
    }
    if (select) {
      win.gBrowser.selectedTab = tab;
    }
    for (const other of [...win.gBrowser.tabs]) {
      if (startupTabs.has(other)) {
        startupTabs.delete(other);
        win.gBrowser.removeTab(other, { animate: false });
      }
    }
    return JSON.stringify(idOf(tab));
  },

  closeTab(id) {
    const win = requireWindow();
    const tab = findTab(win, id);
    if (tab) {
      if (win.gBrowser.tabs.length === 1) {
        // Gecko refuses to have zero tabs; park a blank one Mosaic never sees.
        openingOwnTab = true;
        try {
          const placeholder = win.gBrowser.addTab("about:blank", {
            triggeringPrincipal: SYSTEM_PRINCIPAL,
          });
          startupTabs.add(placeholder);
        } finally {
          openingOwnTab = false;
        }
      }
      win.gBrowser.removeTab(tab, { animate: false });
    }
    return "true";
  },

  selectTab(id) {
    const { win, tab } = requireTab(id);
    win.gBrowser.selectedTab = tab;
    return "true";
  },

  navigate(id, url) {
    load(requireTab(id).browser, url);
    return "true";
  },

  goBack(id) {
    requireTab(id).browser.goBack();
    return "true";
  },

  goForward(id) {
    requireTab(id).browser.goForward();
    return "true";
  },

  reload(id) {
    requireTab(id).browser.reload();
    return "true";
  },

  stop(id) {
    requireTab(id).browser.stop();
    return "true";
  },

  /** Gives keyboard focus to the page in the selected tab. */
  focusContent() {
    const win = requireWindow();
    win.gBrowser.selectedBrowser.focus();
    return "true";
  },

  // ---- Content blocker ----------------------------------------------------

  /**
   * Loads the blocking engine and its filter lists, replacing any engine
   * already in use (which keeps working until the new one is ready).
   *   bundlePath      ublock-engine.js
   *   suffixListPath  public_suffix_list.dat
   *   listPaths       filter list files
   * Returns the number of network filters in effect.
   */
  async configureBlocker(bundlePath, suffixListPath, listPaths, enabled, allowlist) {
    blocker.enabled = !!enabled;
    blocker.allowlist = new Set(allowlist);

    // A sandbox of its own: the bundle installs a few globals, which must not
    // leak into the global that all of Gecko's system modules share.
    const sandbox = Cu.Sandbox(SYSTEM_PRINCIPAL, {
      sandboxName: "Mosaic content blocker",
      wantGlobalProperties: ["TextDecoder", "TextEncoder", "URL", "structuredClone", "atob", "btoa"],
      freshCompartment: true,
    });
    // Privileged code may not load scripts from file: URLs, so the bundle's
    // folder is mapped to resource://mosaic-blocker/, the way the agent's own
    // folder is mapped to resource://mosaic/.
    const bundle = Cc["@mozilla.org/file/local;1"].createInstance(Ci.nsIFile);
    bundle.initWithPath(bundlePath);
    Services.io
      .getProtocolHandler("resource")
      .QueryInterface(Ci.nsISubstitutingProtocolHandler)
      .setSubstitution("mosaic-blocker", Services.io.newFileURI(bundle.parent));
    Services.scriptloader.loadSubScript(`resource://mosaic-blocker/${bundle.leafName}`, sandbox);
    const engine = sandbox.MosaicBlocker;

    engine.init(await IOUtils.readUTF8(suffixListPath));
    engine.beginLists();
    for (const path of listPaths) {
      const text = await IOUtils.readUTF8(path);
      await nextTick();
      engine.addList(PathUtils.filename(path), text);
      await nextTick();
    }
    const count = engine.commitLists();

    blocker.engine = engine;
    if (!blocker.observing) {
      Services.obs.addObserver(onHttpRequest, "http-on-modify-request");
      blocker.observing = true;
    }
    return JSON.stringify({ filters: count, version: engine.version });
  },

  /** Updates the master switch and the per-site exceptions. */
  setBlockerOptions(enabled, allowlist) {
    blocker.enabled = !!enabled;
    blocker.allowlist = new Set(allowlist);
    return "true";
  },

  // ---- Extensions ---------------------------------------------------------

  /**
   * Loads an unpacked, converted Chrome extension from `path`. It is installed
   * as a temporary add-on, so Mosaic re-installs its library on every start.
   * `quiet` marks such a re-install: the extension's "thanks for installing"
   * tab, if it opens one, is suppressed.
   */
  async installExtension(path, quiet) {
    const file = Cc["@mozilla.org/file/local;1"].createInstance(Ci.nsIFile);
    file.initWithPath(path);
    if (quiet) {
      quietUntil = Date.now() + 6000;
    }
    const addon = await lazy.AddonManager.installTemporaryAddon(file);
    if (quiet) {
      quietUntil = Date.now() + 4000;
    }

    // Chrome grants MV3 host permissions at install time; make Gecko match so
    // the extension works without a permissions UI Mosaic does not show yet.
    try {
      const extension = WebExtensionPolicy.getByID(addon.id)?.extension;
      const origins = extension?.manifest?.host_permissions ?? [];
      if (extension && origins.length) {
        await lazy.ExtensionPermissions.add(addon.id, { permissions: [], origins }, extension);
      }
    } catch (error) {
      console.error("Mosaic: could not grant host permissions", error);
    }
    return JSON.stringify(describeAddon(addon));
  },

  async uninstallExtension(id) {
    const addon = await lazy.AddonManager.getAddonByID(id);
    if (addon) {
      await addon.uninstall();
    }
    return JSON.stringify(!!addon);
  },

  async listExtensions() {
    const addons = await lazy.AddonManager.getAddonsByTypes(["extension"]);
    return JSON.stringify(
      addons.filter(addon => !addon.isSystem && !addon.isBuiltin).map(describeAddon)
    );
  },

  /** Opens an extension's popup or options page as a tab. Returns the new tab ID or null. */
  openExtensionPage(id, kind) {
    const url = extensionPage(id, kind);
    if (!url) {
      return "null";
    }
    return this.openTab(url, true);
  },

  // ---- Downloads (downloads area) -------------------------------------------

  /**
   * Starts watching Gecko's downloads for Mosaic and sets how Gecko saves them:
   *   directory  folder new downloads are saved in, without asking
   *   fdmMenu    whether link context menus offer Free Download Manager
   * Safe to call again (on every Gecko start, or when the options change).
   */
  async configureDownloads(options) {
    const { directory, fdmMenu } = options ?? {};
    if (directory) {
      Services.prefs.setIntPref("browser.download.folderList", 2);
      Services.prefs.setStringPref("browser.download.dir", directory);
    }
    // Mosaic asks where to save (when the user wants that) in its own UI, and shows
    // downloads in its own list, so Gecko's dialogs and panel stay out of the way.
    Services.prefs.setBoolPref("browser.download.useDownloadDir", true);
    Services.prefs.setBoolPref("browser.download.always_ask_before_handling_new_types", false);
    Services.prefs.setBoolPref("browser.download.alwaysOpenPanel", false);
    Services.prefs.setBoolPref("browser.download.panel.shown", true);
    Services.prefs.setBoolPref("browser.download.start_downloads_in_tmp_dir", false);
    // Download checks stay on this computer: nothing about the file is sent to a
    // reputation service.
    Services.prefs.setBoolPref("browser.safebrowsing.downloads.remote.enabled", false);
    downloads.fdmMenu = !!fdmMenu;
    await watchDownloads();
    hookDownloadsWindow(browserWindow());
    return JSON.stringify({ watching: true, count: downloads.byId.size });
  },

  /**
   * Every download, plus requests queued for Mosaic, unless nothing changed since
   * `sinceVersion`: then just `{version, unchanged: true}`.
   */
  downloadsState(sinceVersion) {
    if (sinceVersion === downloads.version && downloads.commands.length === 0) {
      return JSON.stringify({ version: downloads.version, unchanged: true });
    }
    return JSON.stringify({
      version: downloads.version,
      watching: downloads.list !== null,
      downloads: [...downloads.byId.values()].map(describeDownload),
      commands: downloads.commands.splice(0),
    });
  },

  /** pause | resume | retry | cancel (keeps it listed, can be retried) | remove (from the list; the file stays). */
  async downloadAction(id, action) {
    const download = downloads.byId.get(id);
    if (!download) {
      throw new Error(`no download ${id}`);
    }
    switch (action) {
      case "pause":
        downloads.paused.add(download);
        download.tryToKeepPartialData = true;
        await download.cancel();
        break;
      case "resume":
      case "retry":
        downloads.paused.delete(download);
        download.start().catch(() => {});
        break;
      case "cancel":
        downloads.paused.delete(download);
        await download.cancel();
        await download.removePartialData();
        break;
      case "remove":
        downloads.paused.delete(download);
        if (!download.stopped || download.hasPartialData) {
          await download.finalize(true);
        }
        await downloads.list?.remove(download);
        untrackDownload(download);
        break;
      default:
        throw new Error(`unknown download action ${action}`);
    }
    downloads.version++;
    return "true";
  },

  /** Downloads `url` to `targetPath` (a full path). Returns the new download's ID. */
  async startDownload(url, targetPath) {
    await watchDownloads();
    const download = await downloadsLazy.Downloads.createDownload({
      source: { url },
      target: { path: targetPath, partFilePath: `${targetPath}.part` },
    });
    download.tryToKeepPartialData = true;
    await downloads.list.add(download);
    download.start().catch(() => {});
    return JSON.stringify(downloads.ids.get(download) ?? null);
  },

  /**
   * What another download program needs to fetch `url` the way Gecko would: the
   * User-Agent, and (only when `includeCookies`) the Cookie header.
   */
  downloadRequestInfo(url, includeCookies) {
    const userAgent = Cc["@mozilla.org/network/protocol;1?name=http"]
      .getService(Ci.nsIHttpProtocolHandler).userAgent;
    let cookies = "";
    if (includeCookies) {
      try {
        cookies = cookieHeaderFor(url);
      } catch (_) {}
    }
    return JSON.stringify({ userAgent, cookies });
  },

  // ---- Passwords (area: passwords) ----------------------------------------

  /**
   * Starts password support: registers the window actors that watch sign-in forms.
   * With `options.separateQueue` (Windows), each password event goes into a queue of its
   * own, read with passwordsTake(), and state() reports only a bare "passwords" marker.
   * Without options (macOS) events go into state() whole, as before.
   */
  async passwordsStart(options) {
    const separate = !!options?.separateQueue;
    const result = await passwords().start({
      push: event => {
        if (!separate) {
          pendingEvents.push(event);
          return;
        }
        passwordEvents.push(event.passwords);
        if (passwordEvents.length > MAX_PASSWORD_EVENTS) {
          passwordEvents.splice(0, passwordEvents.length - MAX_PASSWORD_EVENTS);
        }
        pendingEvents.push({ type: "passwords" });
      },
      tabIdFor: browser => {
        const tab = browserWindow()?.gBrowser?.getTabForBrowser(browser);
        return tab && !startupTabs.has(tab) ? idOf(tab) : null;
      },
    });
    return JSON.stringify(result);
  },

  /**
   * Fills a saved credential into tab `id`, only into frames whose origin is exactly
   * `origin` (and only if the tab's page has that origin). Returns the fields filled.
   */
  async passwordsFill(id, origin, username, password) {
    const count = await passwords().fill(requireTab(id).browser, origin, "MosaicPasswords:fill", { username, password });
    return JSON.stringify(count);
  },

  /** The password events queued since the last call (see passwordsStart's `separateQueue`). */
  passwordsTake() {
    return JSON.stringify(passwordEvents.splice(0));
  },

  /** Puts a generated password into the new-password fields of tab `id`. Returns the fields filled. */
  async passwordsFillGenerated(id, origin, password) {
    const count = await passwords().fill(requireTab(id).browser, origin, "MosaicPasswords:fillGenerated", { password });
    return JSON.stringify(count);
  },

  // ---- Customised shortcuts and reader view (productivity) ---------------

  /**
   * Replaces the list of Mosaic shortcuts to forward while a page has focus:
   * `pairs` is [[chord, eventType], …] (see chordOf for the chord format).
   */
  setForwardedShortcuts(pairs) {
    customShortcuts.clear();
    for (const [chord, type] of pairs) {
      customShortcuts.set(String(chord), String(type));
    }
    return JSON.stringify(customShortcuts.size);
  },

  /**
   * Reader view state of a tab: `available` once Gecko has judged the page to be an
   * article, `active` while the tab shows about:reader, with the original address.
   */
  readerInfo(id) {
    const { browser } = requireTab(id);
    const spec = browser.currentURI?.spec ?? "";
    const original = readerOriginalURL(spec);
    return JSON.stringify({ available: !!browser.isArticle || original !== null, active: original !== null, original });
  },

  /**
   * Shows the tab in Gecko's reader view (`on` true) or returns to the page (false).
   * Entering goes through the page's AboutReader actor, as Firefox's own reader button
   * does, so the article it already parsed is reused. Leaving steps back in history when
   * the previous entry is the original page (so it is not reloaded), else loads it.
   */
  setReaderMode(id, on) {
    const { browser } = requireTab(id);
    const spec = browser.currentURI?.spec ?? "";
    const original = readerOriginalURL(spec);
    if ((original !== null) === !!on) {
      return "true";
    }
    if (on) {
      const actor = browser.browsingContext?.currentWindowGlobal?.getActor("AboutReader");
      if (actor) {
        actor.sendAsyncMessage("Reader:ToggleReaderMode", {});
      } else {
        load(browser, "about:reader?url=" + encodeURIComponent(spec));
      }
      return "true";
    }
    let previous = null;
    try {
      const history = browser.browsingContext.sessionHistory;
      previous = history.index > 0 ? history.getEntryAtIndex(history.index - 1).URI.spec : null;
    } catch (_) {}
    if (browser.canGoBack && previous === original) {
      browser.goBack();
    } else {
      load(browser, original);
    }
    return "true";
  },

  // ---- Page features (engine area) -----------------------------------------

  /**
   * Routes permission requests, blocked pop-ups and page full screen to Mosaic
   * (events in `state()`), and silences Firefox's find-bar sounds. Call once
   * after `setup`. Options:
   *   fakeMedia  test runs only: fake camera/microphone streams and a fixed
   *              location, so no system permission dialog appears.
   */
  configurePageFeatures(options = {}) {
    const win = requireWindow();
    if (!page.configured) {
      page.configured = true;
      Services.prefs.setBoolPref("accessibility.typeaheadfind.enablesound", false);
      const { Integration } = ChromeUtils.importESModule("resource://gre/modules/Integration.sys.mjs");
      Integration.contentPermission.register(contentPermissionOverride);
      patchWebRTC();
      // Capture phase on the window, ahead of Firefox's own listener on gBrowser.
      win.addEventListener("DOMUpdateBlockedPopups", onBlockedPopups, true);
      watchFullscreen(win);
      // Context-menu items for Firefox features Mosaic does not use (its bookmarks, its
      // account's "send to device", its AI chat): they would act on data Mosaic never shows.
      const hidden = ["context-bookmarkpage", "context-bookmarklink", "context-bookmarkframe",
        "context-sendpagetodevice", "context-sep-sendpagetodevice", "context-sendlinktodevice",
        "context-sep-sendlinktodevice", "context-ask-chat"];
      // The menu is native on macOS, so CSS cannot hide items: they are marked hidden after
      // Firefox's own handler (registered first) has prepared the menu.
      const menu = win.document.getElementById("contentAreaContextMenu");
      menu?.addEventListener("popupshowing", event => {
        if (event.target === menu) {
          for (const id of hidden) {
            win.document.getElementById(id)?.setAttribute("hidden", "true");
          }
        }
      });
    }
    if (options.fakeMedia) {
      Services.prefs.setBoolPref("media.navigator.streams.fake", true);
      Services.prefs.setBoolPref("geo.provider.use_corelocation", false);
      Services.prefs.setStringPref("geo.provider.network.url",
        'data:application/json,{"location":{"lat":51.5007,"lng":-0.1246},"accuracy":20}');
    }
    return "true";
  },

  /** Current values of the reader preferences Mosaic manages (READER_PREFS). */
  readerPrefs() {
    const prefs = {};
    for (const [name, type] of Object.entries(READER_PREFS)) {
      if (Services.prefs.getPrefType(name) === Services.prefs.PREF_INVALID) {
        continue;
      }
      prefs[name] = type === "int" ? Services.prefs.getIntPref(name) : Services.prefs.getCharPref(name);
    }
    return JSON.stringify(prefs);
  },

  /** Sets reader preferences (only the names in READER_PREFS); open reader pages follow. */
  setReaderPrefs(prefs) {
    for (const [name, value] of Object.entries(prefs ?? {})) {
      const type = READER_PREFS[name];
      if (type === "int") {
        Services.prefs.setIntPref(name, Math.round(Number(value)));
      } else if (type === "char") {
        Services.prefs.setCharPref(name, String(value));
      }
    }
    return "true";
  },

  /** Per tab: its zoom (1 = 100 %) and whether it is muted. */
  pageActivity() {
    const win = browserWindow();
    if (!win || !win.gBrowser) {
      return "[]";
    }
    const tabs = [];
    for (const tab of win.gBrowser.tabs) {
      if (startupTabs.has(tab) || tab.closing || !tab.linkedPanel) {
        continue;
      }
      let zoom = 1;
      try {
        zoom = win.ZoomManager.getZoomForBrowser(tab.linkedBrowser);
      } catch (_) {}
      tabs.push({ id: idOf(tab), zoom, muted: !!tab.muted });
    }
    return JSON.stringify(tabs);
  },

  /** Answers a `permissionRequest` event. Returns false if it is no longer pending. */
  answerPermission(requestId, allow) {
    const record = page.requests.get(requestId);
    if (!record) {
      return "false";
    }
    page.requests.delete(requestId);
    try {
      if (record.type === "webrtc") {
        answerWebRTC(record, !!allow);
      } else if (allow) {
        record.request.allow();
      } else {
        record.request.cancel();
      }
    } catch (error) {
      console.error("Mosaic: answering a permission request", error);
      return JSON.stringify(String(error?.message ?? error));
    }
    return "true";
  },

  /**
   * Mirrors Mosaic's site decisions into Gecko's permission manager, so pages
   * see them (Notification.permission, repeat requests, pop-up blocking).
   * `entries`: [{ origin, permission: "location"|"notifications"|"popups", decision: "allow"|"block"|null }]
   */
  setSitePermissions(entries) {
    const { perms } = Services;
    for (const { origin, permission, decision } of entries) {
      const key = GECKO_PERMISSION_KEYS[permission];
      if (!key || origin === "file://") {
        continue;
      }
      let principal;
      try {
        principal = Services.scriptSecurityManager.createContentPrincipalFromOrigin(origin);
      } catch (_) {
        continue;
      }
      if (decision === "allow" || decision === "block") {
        perms.addFromPrincipal(principal, key, decision === "allow" ? perms.ALLOW_ACTION : perms.DENY_ACTION);
      } else {
        perms.removeFromPrincipal(principal, key);
      }
    }
    return "true";
  },

  /** Opens the pop-ups the page in tab `id` was stopped from opening. */
  async openBlockedPopups(id) {
    await requireTab(id).browser.popupAndRedirectBlocker?.unblockAllPopups();
    return "true";
  },

  /**
   * Finds `text` in tab `id` and highlights every match. `options`:
   * { again: next/previous match of the same text, backwards, caseSensitive }.
   * Resolves to { found, wrapped, current, total, limit }.
   */
  find(id, text, options = {}) {
    const { browser } = requireTab(id);
    const finder = browser.finder;
    const listener = findListenerFor(browser);
    let session = page.findSessions.get(browser);
    if (!session) {
      session = { caseSensitive: null };
      page.findSessions.set(browser, session);
      finder.onFindbarOpen();
      finder.onHighlightAllChange(true);
    }
    const caseSensitive = !!options.caseSensitive;
    if (session.caseSensitive !== caseSensitive) {
      session.caseSensitive = caseSensitive;
      finder.caseSensitive = caseSensitive;
    }
    return new Promise(resolve => {
      const timer = setTimeout(() => waiter.finish(null), 2000);
      const waiter = {
        text,
        result: null,
        finish(value) {
          clearTimeout(timer);
          if (listener.waiter === waiter) {
            listener.waiter = null;
          }
          if (value?.found) {
            // Every match, as Firefox's find bar does with "Highlight All".
            finder.highlight(true, text, false);
          }
          resolve(JSON.stringify(value ?? { found: waiter.result === 0 || waiter.result === 2, wrapped: false,
            current: 0, total: 0, limit: 0, timedOut: true }));
        },
      };
      listener.waiter?.finish(null);
      listener.waiter = waiter;
      if (options.again) {
        finder.findAgain(text, !!options.backwards, false, false);
      } else {
        finder.fastFind(text, false, false);
      }
    });
  },

  /** Ends the find session in tab `id`: highlights go, the last match stays selected. */
  stopFind(id) {
    const { browser } = requireTab(id);
    if (page.findSessions.has(browser)) {
      page.findSessions.delete(browser);
      browser.finder.onFindbarClose();
    }
    return "true";
  },

  /** Sets the zoom of tab `id` (1 = 100 %). Gecko remembers it for the site too. */
  setZoom(id, factor) {
    const { win, browser } = requireTab(id);
    if (Math.abs(factor - 1) < 0.005) {
      win.FullZoom.reset(browser);
      win.ZoomManager.setZoomForBrowser(browser, 1);
    } else {
      win.FullZoom.setZoom(factor, browser);
    }
    return JSON.stringify(win.ZoomManager.getZoomForBrowser(browser));
  },

  /** Opens Firefox's print dialog (tab-modal, inside the page area) for tab `id`. */
  print(id) {
    const { win, tab, browser } = requireTab(id);
    win.gBrowser.selectedTab = tab;
    win.PrintUtils.startPrintWindow(browser.browsingContext);
    return "true";
  },

  /** Saves tab `id` as a PDF at `path`, without any dialog. */
  async saveAsPDF(id, path) {
    const { browser } = requireTab(id);
    const settings = Cc["@mozilla.org/gfx/printsettings-service;1"]
      .getService(Ci.nsIPrintSettingsService)
      .createNewPrintSettings();
    settings.printerName = "";
    settings.isInitializedFromPrinter = true;
    settings.isInitializedFromPrefs = true;
    settings.outputDestination = Ci.nsIPrintSettings.kOutputDestinationFile;
    settings.outputFormat = Ci.nsIPrintSettings.kOutputFormatPDF;
    settings.toFileName = path;
    settings.printSilent = true;
    await browser.browsingContext.print(settings);
    return "true";
  },

  /** Dialogs open over tab `id` (alerts, print, …), by their document URL. */
  dialogs(id) {
    const { win, browser } = requireTab(id);
    const box = win.gBrowser.getTabDialogBox(browser);
    const urls = [box._tabDialogManager, box._contentDialogManager]
      .flatMap(manager => manager?._dialogs ?? [])
      .map(dialog => dialog._openedURL ?? "");
    return JSON.stringify(urls);
  },

  /** Closes every dialog over tab `id`, as if each were cancelled. */
  closeDialogs(id) {
    const { win, browser } = requireTab(id);
    win.gBrowser.getTabDialogBox(browser).abortAllDialogs();
    return "true";
  },

  /** Opens or closes the developer tools of tab `id` (docked in the page area). Returns whether they are open. */
  async toggleDevTools(id) {
    const { win, tab } = requireTab(id);
    win.gBrowser.selectedTab = tab;
    const { require } = ChromeUtils.importESModule("resource://devtools/shared/loader/Loader.sys.mjs");
    const { gDevTools } = require("devtools/client/framework/devtools");
    if (gDevTools.getToolboxForTab(tab)) {
      await gDevTools.closeToolboxForTab(tab);
      return "false";
    }
    await gDevTools.showToolboxForTab(tab, { hostType: "bottom" });
    return "true";
  },

  setMuted(id, muted) {
    const { tab } = requireTab(id);
    if (tab.muted !== !!muted) {
      tab.toggleMuteAudio();
    }
    return JSON.stringify(tab.muted);
  },

  /**
   * Saves a PNG of tab `id` at `path`: the visible area, or the whole page
   * (cut off at 16384 CSS pixels tall). Resolves to { width, height, cropped }.
   */
  async screenshot(id, path, fullPage) {
    const { win, browser } = requireTab(id);
    const scale = win.devicePixelRatio || 1;
    if (!fullPage) {
      const size = await snapshotPNG(browser, null, browser.clientWidth, browser.clientHeight, scale, path);
      return JSON.stringify({ ...size, cropped: false });
    }
    const info = await browser.browsingContext.currentWindowGlobal
      .getActor("Thumbnails")
      .sendQuery("Browser:Thumbnail:ContentInfo");
    const maxHeight = 16384;
    const height = Math.min(Math.max(info.documentHeight, info.height), maxHeight);
    const size = await snapshotPNG(browser, { x: 0, y: 0 }, info.width, height, scale, path);
    return JSON.stringify({ ...size, cropped: info.documentHeight > maxHeight });
  },

  /** Puts the most likely video of tab `id` into Picture-in-Picture (or takes it out). */
  togglePictureInPicture(id) {
    const { browser } = requireTab(id);
    browser.browsingContext.currentWindowGlobal
      .getActor("PictureInPictureLauncher")
      .sendAsyncMessage("PictureInPicture:KeyToggle");
    return "true";
  },

  /**
   * A real (trusted) mouse click at (x, y) in tab `id`'s page, CSS pixels; button
   * 0 left, 2 right (which opens the context menu). For scripted checks: it
   * counts as a user gesture.
   */
  synthesizeClick(id, x, y, button = 0) {
    const { win, tab, browser } = requireTab(id);
    win.gBrowser.selectedTab = tab;
    const rect = browser.getBoundingClientRect();
    const send = type => {
      if (typeof win.synthesizeMouseEvent === "function") {
        // Newer Gecko (ESR 153): a chrome-window method. Not marked as DOM-synthesized,
        // so the page treats it as real input (user activation).
        win.synthesizeMouseEvent(type, rect.left + x, rect.top + y,
          { button, buttons: type === "mousedown" ? 1 << (button === 2 ? 1 : 0) : 0, clickCount: 1, modifiers: 0 },
          { isDOMEventSynthesized: false });
      } else {
        win.windowUtils.sendMouseEvent(type, rect.left + x, rect.top + y, button, 1, 0);
      }
    };
    send("mousedown");
    send("mouseup");
    if (button === 2) {
      send("contextmenu");
    }
    return "true";
  },

  /**
   * Popups open in the Gecko window (context menu, the clipboard "Paste" button, …), by
   * id; closes them if `close`. For scripted checks, which must not leave one open.
   */
  openPopups(close) {
    const doc = requireWindow().document;
    const open = [...doc.querySelectorAll("menupopup, panel")].filter(popup => popup.state === "open");
    if (close) {
      for (const popup of open) {
        popup.hidePopup();
      }
    }
    return JSON.stringify(open.map(popup => popup.id || popup.localName));
  },

  /** Leaves page full screen, if the page is in it. */
  exitFullscreen() {
    const win = requireWindow();
    if (win.document.fullscreenElement) {
      win.document.exitFullscreen();
    }
    return "true";
  },

  /** Sizes of the window's top-level boxes; for diagnosing leftover chrome. */
  debugLayout() {
    const win = requireWindow();
    const doc = win.document;
    const describe = element => {
      const rect = element.getBoundingClientRect();
      return {
        tag: element.localName,
        id: element.id,
        cls: String(element.className || "").slice(0, 60),
        y: Math.round(rect.y),
        height: Math.round(rect.height),
        hidden: element.hidden,
        collapsed: element.collapsed,
      };
    };
    const out = { inner: [win.innerWidth, win.innerHeight], outer: [win.outerWidth, win.outerHeight], boxes: [] };
    const walk = (element, depth) => {
      if (depth > 3) return;
      for (const child of element.children) {
        const info = describe(child);
        if (info.height > 0 || depth < 2) {
          out.boxes.push({ depth, ...info });
          walk(child, depth + 1);
        }
      }
    };
    walk(doc.documentElement, 0);
    out.at = {};
    for (const [x, y] of [[5, 5], [5, 20], [600, 20], [600, 60]]) {
      out.at[`${x},${y}`] = doc.elementsFromPoint(x, y).map(element => {
        const rect = element.getBoundingClientRect();
        const style = win.getComputedStyle(element);
        return `${element.localName}#${element.id}.${String(element.className).slice(0, 30)}` +
          ` [${Math.round(rect.y)}+${Math.round(rect.height)}] bg=${style.backgroundColor}` +
          ` pad=${style.paddingTop} mar=${style.marginTop}`;
      });
    }
    const selected = win.gBrowser.selectedBrowser.getBoundingClientRect();
    out.selectedBrowser = [Math.round(selected.y), Math.round(selected.height)];
    out.rootAttrs = [...doc.documentElement.attributes].map(a => `${a.name}=${a.value}`.slice(0, 50));
    return JSON.stringify(out);
  },

  /**
   * Quits Gecko in an orderly way, so the profile is flushed, without giving
   * pages or prompts a chance to veto. The debugging connection drops as part
   * of the shutdown, usually before this call's own reply is delivered.
   */
  quit() {
    Services.startup.quit(Ci.nsIAppStartup.eForceQuit);
    return "true";
  },

  /**
   * Describes the engine. `pid` is the browser (parent) process, which on
   * Windows is not necessarily the process Mosaic launched: Firefox's launcher
   * process starts the real browser as its child. `windowHandle` is the
   * browser window's native handle as a decimal string, "0" while there is no
   * window yet or in headless mode; see `nativeHandle`.
   */
  version() {
    const win = browserWindow();
    return JSON.stringify({
      agent: 2,
      gecko: Services.appinfo.platformVersion,
      app: Services.appinfo.name,
      appVersion: Services.appinfo.version,
      pid: Services.appinfo.processID,
      windowHandle: win ? nativeHandle(win) : "0",
    });
  },

  // ---- Platform area: safe engine switching, languages ----------------------
  // Additive; platform-neutral. Mosaic loads a page in a background tab before
  // moving a tab to Gecko, and keeps the old engine's page until it has loaded.

  /**
   * Load state of one tab: whether it is still loading, the document it shows
   * and, if that document is one of Gecko's error pages (network, certificate,
   * blocked), the error code from its address ("dnsNotFound", …), else null.
   */
  pageStatus(id) {
    const { tab, browser } = requireTab(id);
    let documentURI = "";
    try {
      documentURI = browser.browsingContext?.currentWindowGlobal?.documentURI?.spec ?? "";
    } catch (_) {}
    let error = null;
    if (/^about:(neterror|certerror|blocked|httpsonlyerror)/.test(documentURI)) {
      const match = /[?&]e=([^&]*)/.exec(documentURI);
      error = match ? decodeURIComponent(match[1]) : documentURI.split("?")[0];
    }
    return JSON.stringify({
      loading: tab.hasAttribute("busy"),
      url: browser.currentURI?.spec ?? "",
      document: documentURI,
      title: tab.label ?? "",
      error,
    });
  },

  /**
   * Languages pages are asked for (Accept-Language, navigator.languages): a
   * comma-separated list of BCP 47 tags, most preferred first. Applies to
   * documents loaded from now on.
   */
  setAcceptLanguages(list) {
    Services.prefs.setStringPref("intl.accept_languages", String(list));
    return JSON.stringify(Services.prefs.getStringPref("intl.accept_languages", ""));
  },
};

// ---- Tabs area: sleeping tabs and tab shortcuts -------------------------------
//
// Additions owned by Mosaic's tabs area, kept in this section. Platform-neutral.

// Shortcuts pressed while a Gecko page has focus that belong to Mosaic's tab handling:
// reopen closed tab, tab search, ⌘1–⌘9, next/previous tab. Without these, Gecko would act
// on its own (hidden) tab strip: reopen one of its closed tabs, open its add-ons page,
// or switch between Gecko tabs only.
FORWARDED_WITH_SHIFT.t = "tabs.reopenClosed";
FORWARDED_WITH_SHIFT.a = "tabs.search";
FORWARDED_WITH_SHIFT["}"] = "tabs.next";
FORWARDED_WITH_SHIFT["{"] = "tabs.previous";
for (let number = 1; number <= 9; number++) {
  FORWARDED[String(number)] = `tabs.select${number}`;
}

Object.assign(Agent, {
  /**
   * Unloads a background tab to free memory, keeping the tab, its history and scroll
   * position; Gecko restores it when the tab is selected again. `force` skips the page's
   * chance to refuse (a beforeunload handler, an open dialog). Gecko never discards its
   * selected tab, so if this one is selected (Mosaic is showing a Chromium tab), Gecko is
   * first switched to another loaded tab, or to a hidden blank one. Returns true if the
   * tab is now discarded.
   */
  async discardTab(id, force) {
    const { win, tab } = requireTab(id);
    const gBrowser = win.gBrowser;
    if (!tab.linkedPanel) {
      return JSON.stringify(true);
    }
    if (tab.selected) {
      const loaded = gBrowser.tabs.filter(other => other !== tab && other.linkedPanel && !other.closing);
      const parked = loaded.find(other => startupTabs.has(other)) ??
        loaded.find(other => !startupTabs.has(other));
      if (parked) {
        gBrowser.selectedTab = parked;
      } else {
        openingOwnTab = true;
        try {
          const placeholder = gBrowser.addTab("about:blank", { triggeringPrincipal: SYSTEM_PRINCIPAL });
          startupTabs.add(placeholder);
          gBrowser.selectedTab = placeholder;
        } finally {
          openingOwnTab = false;
        }
      }
    }
    // Lets session history catch up, so the restored tab has its latest state.
    if (typeof gBrowser.prepareDiscardBrowser === "function") {
      await gBrowser.prepareDiscardBrowser(tab);
    }
    // Re-checks everything, including that the tab was not selected meanwhile.
    return JSON.stringify(!!gBrowser.discardBrowser(tab, !!force));
  },

  /** Per tab: whether it is playing sound, and whether it is discarded. */
  tabActivity() {
    const win = browserWindow();
    if (!win || !win.gBrowser) {
      return "[]";
    }
    const tabs = [];
    for (const tab of win.gBrowser.tabs) {
      if (startupTabs.has(tab) || tab.closing) {
        continue;
      }
      tabs.push({
        id: idOf(tab),
        audible: tab.hasAttribute("soundplaying"),
        discarded: !tab.linkedPanel,
      });
    }
    return JSON.stringify(tabs);
  },
});
// ---- Privacy (privacy area) -------------------------------------------------
//
// Additive section owned by the privacy area: cosmetic filtering, the blocker
// dashboard's request log, privacy preferences (HTTPS-Only, query stripping,
// cookies, DNS over HTTPS), site data and clearing browsing data. Platform-
// neutral; nothing above depends on it apart from two hook calls in
// `onHttpRequest`. Methods are added to `Agent` at the end.

const privacy = {
  cosmeticEnabled: true,
  /** The engine whose generic stylesheet content processes currently have. */
  cosmeticEngine: null,
  cosmeticGeneration: 0,
  /** hostname -> { css, exceptions }, for the current engine. */
  cosmeticHosts: new Map(),
  cosmeticListening: false,
  /** tab -> blocked requests of its current page: [{ url, type, page, count }] */
  blockedLog: new WeakMap(),
  options: null,
};

const COSMETIC_GENERIC_KEY = "mosaic:cosmetic-generic";

function privacyNoteBlocked(tab, url, type, page) {
  let entries = privacy.blockedLog.get(tab);
  if (!entries) {
    entries = [];
    privacy.blockedLog.set(tab, entries);
  }
  const existing = entries.find(entry => entry.url === url && entry.type === type);
  if (existing) {
    existing.count += 1;
  } else if (entries.length < 500) {
    entries.push({ url, type, page, count: 1 });
  }
}

function privacyResetBlocked(tab) {
  privacy.blockedLog.delete(tab);
}

/**
 * Runs in every content process (a process script, so no files need to be
 * readable from the content sandbox). On each new http(s) document it asks the
 * parent which cosmetic CSS applies, then adds it as an author sheet through
 * nsIDOMWindowUtils, which page CSP does not restrict. The generic sheet comes
 * through sharedData and is parsed once per process.
 */
function mosaicCosmeticProcessScript() {
  "use strict";
  const { classes: Cc, interfaces: Ci, utils: Cu } = Components;
  const AUTHOR = Ci.nsIStyleSheetService.AUTHOR_SHEET;
  const service = Cc["@mozilla.org/content/style-sheet-service;1"].getService(Ci.nsIStyleSheetService);
  const preload = css =>
    service.preloadSheet(Services.io.newURI("data:text/css;charset=utf-8," + encodeURIComponent(css)), AUTHOR);
  let generic = { generation: -1, sheet: null };
  const sheets = new Map();
  const sheetFor = css => {
    let sheet = sheets.get(css);
    if (!sheet) {
      sheet = preload(css);
      sheets.set(css, sheet);
      if (sheets.size > 64) {
        sheets.delete(sheets.keys().next().value);
      }
    }
    return sheet;
  };
  const pending = new Map();
  let nextId = 1;
  Services.obs.addObserver(
    {
      observe(doc) {
        try {
          const win = doc.defaultView;
          const url = doc.documentURI || "";
          if (!win || !/^https?:/.test(url)) {
            return;
          }
          const id = nextId++;
          pending.set(id, Cu.getWeakReference(doc));
          const context = win.browsingContext;
          sendAsyncMessage("Mosaic:Cosmetic", {
            id,
            url,
            host: doc.location?.hostname ?? "",
            top: context?.top?.id ?? 0,
            isTop: !context?.parent,
          });
        } catch (_) {}
      },
    },
    "document-element-inserted"
  );
  addMessageListener("Mosaic:CosmeticReply", ({ data }) => {
    try {
      const ref = pending.get(data.id);
      pending.delete(data.id);
      const utils = ref?.get()?.defaultView?.windowUtils;
      if (!utils) {
        return;
      }
      if (data.generic) {
        const shared = Services.cpmm.sharedData.get("mosaic:cosmetic-generic");
        if (shared && shared.generation !== generic.generation) {
          generic = { generation: shared.generation, sheet: shared.css ? preload(shared.css) : null };
        }
        if (generic.sheet) {
          utils.addSheet(generic.sheet, AUTHOR);
        }
      }
      if (data.genericCSS) {
        utils.addSheet(sheetFor(data.genericCSS), AUTHOR);
      }
      if (data.css) {
        utils.addSheet(sheetFor(data.css), AUTHOR);
      }
    } catch (_) {}
  });
}

/** Makes content processes see the current engine's generic stylesheet. */
function publishCosmeticGeneric(engine) {
  if (privacy.cosmeticEngine === engine) {
    return;
  }
  privacy.cosmeticEngine = engine;
  privacy.cosmeticHosts.clear();
  privacy.cosmeticGeneration += 1;
  Services.ppmm.sharedData.set(COSMETIC_GENERIC_KEY, {
    generation: privacy.cosmeticGeneration,
    css: engine.cosmeticGeneric([]),
  });
  Services.ppmm.sharedData.flush();
}

function cosmeticRulesFor(engine, hostname) {
  let rules = privacy.cosmeticHosts.get(hostname);
  if (!rules) {
    const result = engine.cosmeticFor(hostname);
    rules = { css: String(result.css || ""), exceptions: Array.from(result.exceptions || [], String) };
    privacy.cosmeticHosts.set(hostname, rules);
    if (privacy.cosmeticHosts.size > 256) {
      privacy.cosmeticHosts.delete(privacy.cosmeticHosts.keys().next().value);
    }
  }
  return rules;
}

/** Hostname of the top-level page a document belongs to. */
function topHostname(data) {
  if (data.isTop) {
    return data.host;
  }
  try {
    const context = BrowsingContext.get(data.top);
    const uri = context?.currentWindowGlobal?.documentURI ?? context?.currentURI;
    if (uri && (uri.schemeIs("http") || uri.schemeIs("https"))) {
      return uri.host;
    }
  } catch (_) {}
  return data.host;
}

function onCosmeticRequest(message) {
  const data = message.data;
  const reply = { id: data.id, generic: false, css: "", genericCSS: "" };
  try {
    const engine = blocker.engine;
    if (
      engine &&
      typeof engine.cosmeticFor === "function" &&
      blocker.enabled &&
      privacy.cosmeticEnabled &&
      !isAllowlisted(topHostname(data))
    ) {
      publishCosmeticGeneric(engine);
      const rules = cosmeticRulesFor(engine, data.host);
      reply.css = rules.css;
      if (rules.exceptions.length) {
        reply.genericCSS = engine.cosmeticGeneric(rules.exceptions);
      } else {
        reply.generic = true;
      }
    }
  } catch (error) {
    console.error("Mosaic cosmetic filtering:", error);
  }
  message.target.sendAsyncMessage("Mosaic:CosmeticReply", reply);
}

function startCosmeticFiltering() {
  if (privacy.cosmeticListening) {
    return;
  }
  privacy.cosmeticListening = true;
  Services.ppmm.addMessageListener("Mosaic:Cosmetic", onCosmeticRequest);
  const source = `(${mosaicCosmeticProcessScript.toString()})();`;
  Services.ppmm.loadProcessScript(`data:application/javascript;charset=utf-8,${encodeURIComponent(source)}`, true);
}

function setPref(name, value) {
  if (typeof value === "boolean") {
    Services.prefs.setBoolPref(name, value);
  } else if (typeof value === "number") {
    Services.prefs.setIntPref(name, value);
  } else {
    Services.prefs.setStringPref(name, String(value));
  }
}

function readPref(name) {
  switch (Services.prefs.getPrefType(name)) {
    case Services.prefs.PREF_BOOL: return Services.prefs.getBoolPref(name);
    case Services.prefs.PREF_INT: return Services.prefs.getIntPref(name);
    case Services.prefs.PREF_STRING: return Services.prefs.getStringPref(name);
    default: return null;
  }
}

const PRIVACY_PREFS = [
  "dom.security.https_only_mode",
  "dom.security.https_only_mode_pbm",
  "privacy.query_stripping.enabled",
  "privacy.query_stripping.enabled.pbmode",
  "privacy.query_stripping.strip_list",
  "network.cookie.cookieBehavior",
  "network.cookie.cookieBehavior.pbmode",
  "privacy.sanitize.sanitizeOnShutdown",
  "privacy.clearOnShutdown_v2.cookiesAndStorage",
  "network.trr.mode",
  "network.trr.uri",
];

function clearDataFlags(kinds) {
  const I = Ci.nsIClearDataService;
  const siteData = I.CLEAR_COOKIES_AND_SITE_DATA ??
    (I.CLEAR_COOKIES | I.CLEAR_DOM_STORAGES | I.CLEAR_SECURITY_SETTINGS | I.CLEAR_EME | I.CLEAR_MEDIA_DEVICES);
  let flags = 0;
  for (const kind of kinds) {
    switch (kind) {
      case "history": flags |= I.CLEAR_HISTORY | (I.CLEAR_SESSION_HISTORY ?? 0); break;
      case "cookiesAndSiteData": flags |= siteData; break;
      case "cache": flags |= I.CLEAR_ALL_CACHES; break;
      case "downloads": flags |= I.CLEAR_DOWNLOADS; break;
      case "formData": flags |= I.CLEAR_FORMDATA; break;
    }
  }
  return flags;
}

function schemelessSite(host) {
  const bare = host.replace(/^\./, "");
  try {
    if (typeof Services.eTLD.getSchemelessSiteFromHost === "function") {
      return Services.eTLD.getSchemelessSiteFromHost(bare);
    }
    return Services.eTLD.getBaseDomainFromHost(bare);
  } catch (_) {
    return bare; // an IP address or a single-label name is its own site
  }
}

const PrivacyAgent = {
  /**
   * Applies Mosaic's privacy settings to Gecko's preferences (see
   * PrivacyController.configureGecko for the shape of `options`). Also starts
   * cosmetic filtering. Returns the preferences' values afterwards.
   */
  configurePrivacy(options) {
    privacy.options = options;
    privacy.cosmeticEnabled = options.cosmetic !== false;
    setPref("dom.security.https_only_mode", !!options.httpsOnly);
    setPref("dom.security.https_only_mode_pbm", !!options.httpsOnly);
    setPref("privacy.query_stripping.enabled", !!options.stripTracking);
    setPref("privacy.query_stripping.enabled.pbmode", !!options.stripTracking);
    setPref("privacy.query_stripping.strip_list", (options.stripList ?? []).join(" "));
    // 1 = reject third-party cookies; 5 = Firefox's default, which partitions them.
    setPref("network.cookie.cookieBehavior", options.blockThirdPartyCookies ? 1 : 5);
    setPref("network.cookie.cookieBehavior.pbmode", options.blockThirdPartyCookies ? 1 : 5);
    // Clear on exit: cookies and site data only, never history or form data.
    const onExit = !!options.clearOnExit;
    setPref("privacy.sanitize.sanitizeOnShutdown", onExit);
    setPref("privacy.clearOnShutdown_v2.cookiesAndStorage", onExit);
    setPref("privacy.clearOnShutdown.cookies", onExit);
    setPref("privacy.clearOnShutdown.offlineApps", onExit);
    for (const name of [
      "privacy.clearOnShutdown_v2.cache",
      "privacy.clearOnShutdown_v2.browsingHistoryAndDownloads",
      "privacy.clearOnShutdown_v2.historyFormDataAndDownloads",
      "privacy.clearOnShutdown_v2.formdata",
      "privacy.clearOnShutdown_v2.siteSettings",
      "privacy.clearOnShutdown.history",
      "privacy.clearOnShutdown.formdata",
      "privacy.clearOnShutdown.downloads",
      "privacy.clearOnShutdown.sessions",
      "privacy.clearOnShutdown.cache",
      "privacy.clearOnShutdown.siteSettings",
    ]) {
      setPref(name, false);
    }
    const dnsBefore = JSON.stringify([readPref("network.trr.mode"), readPref("network.trr.uri")]);
    for (const [name, value] of Object.entries(options.dnsPreferences ?? {})) {
      setPref(name, value);
    }
    if (dnsBefore !== JSON.stringify([readPref("network.trr.mode"), readPref("network.trr.uri")])) {
      // Names resolved through the previous resolver must not linger.
      try {
        Services.dns.clearCache(true);
      } catch (_) {}
    }
    // Sites allowed over plain HTTP.
    const type = "https-only-load-insecure";
    for (const permission of Services.perms.getAllByTypes([type])) {
      Services.perms.removePermission(permission);
    }
    for (const host of options.httpsExceptions ?? []) {
      for (const scheme of ["http", "https"]) {
        const principal = Services.scriptSecurityManager.createContentPrincipalFromOrigin(`${scheme}://${host}`);
        Services.perms.addFromPrincipal(principal, type, Ci.nsIPermissionManager.ALLOW_ACTION);
      }
    }
    startCosmeticFiltering();
    return this.privacyStatus();
  },

  /** Turns cosmetic filtering on or off; affects documents loaded afterwards. */
  setCosmeticOptions(enabled) {
    privacy.cosmeticEnabled = !!enabled;
    startCosmeticFiltering();
    return "true";
  },

  /** The privacy preferences' current values, for checks and the settings page. */
  privacyStatus() {
    const prefs = {};
    for (const name of PRIVACY_PREFS) {
      prefs[name] = readPref(name);
    }
    let selfTest = null;
    try {
      selfTest = blocker.engine?.match("https://www.google-analytics.com/analytics.js", "https://example.com/", "script") ?? null;
    } catch (error) {
      selfTest = String(error);
    }
    const dns = {};
    for (const name of ["currentTrrMode", "currentTrrURI", "currentTrrConfirmationState"]) {
      try {
        dns[name] = Services.dns[name];
      } catch (_) {}
    }
    return JSON.stringify({
      prefs,
      dns,
      blocker: {
        engine: !!blocker.engine,
        enabled: blocker.enabled,
        observing: blocker.observing,
        allowlist: blocker.allowlist.size,
        selfTest,
      },
      cosmetic: {
        enabled: privacy.cosmeticEnabled,
        listening: privacy.cosmeticListening,
        generation: privacy.cosmeticGeneration,
        hostsCached: privacy.cosmeticHosts.size,
      },
    });
  },

  /**
   * Requests the blocker stopped on a tab's current page, with the filter that
   * matched each: [{ url, type, count, filter }].
   */
  blockedRequests(id) {
    const { tab } = requireTab(id);
    const engine = blocker.engine;
    const entries = privacy.blockedLog.get(tab) ?? [];
    return JSON.stringify(
      entries.map(entry => ({
        url: entry.url,
        type: entry.type,
        count: entry.count,
        filter:
          engine && typeof engine.matchDetails === "function"
            ? String(engine.matchDetails(entry.url, entry.page, entry.type).filter || "")
            : "",
      }))
    );
  },

  /**
   * Hosts with cookies or storage: [{ host, cookies, bytes }]. Private-browsing
   * data is not included (Mosaic has no private Gecko windows).
   */
  async siteData() {
    const hosts = new Map();
    const add = (host, cookies, bytes) => {
      const key = host.replace(/^\./, "").toLowerCase();
      if (!key) {
        return;
      }
      const entry = hosts.get(key) ?? { host: key, cookies: 0, bytes: 0 };
      entry.cookies += cookies;
      entry.bytes += bytes;
      hosts.set(key, entry);
    };
    for (const cookie of Services.cookies.cookies) {
      if (!cookie.originAttributes?.privateBrowsingId) {
        add(cookie.rawHost ?? cookie.host, 1, 0);
      }
    }
    const usage = await new Promise(resolve => {
      try {
        Services.qms.getUsage(request => resolve(request.resultCode === Cr.NS_OK ? request.result : []), false);
      } catch (_) {
        resolve([]);
      }
    });
    for (const item of usage ?? []) {
      try {
        const principal = Services.scriptSecurityManager.createContentPrincipalFromOrigin(item.origin);
        if (principal.schemeIs("http") || principal.schemeIs("https")) {
          add(principal.host, 0, Number(item.usage) || 0);
        }
      } catch (_) {}
    }
    return JSON.stringify([...hosts.values()]);
  },

  /** Deletes cookies, storage and cache of the site `host` belongs to. */
  async clearSiteData(host) {
    const site = schemelessSite(host);
    const flags = clearDataFlags(["cookiesAndSiteData", "cache"]);
    await new Promise(resolve => {
      if (typeof Services.clearData.deleteDataFromSite === "function") {
        Services.clearData.deleteDataFromSite(site, {}, true, flags, resolve);
      } else {
        Services.clearData.deleteDataFromBaseDomain(site, true, flags, resolve);
      }
    });
    return JSON.stringify(site);
  },

  /**
   * Clears browsing data. `kinds`: history, cookiesAndSiteData, cache,
   * downloads, formData. `sinceMs`: start of the range (ms since 1970), or 0
   * for everything.
   */
  async clearBrowsingData(kinds, sinceMs) {
    const flags = clearDataFlags(kinds);
    if (!flags) {
      return "true";
    }
    await new Promise(resolve => {
      if (sinceMs > 0) {
        Services.clearData.deleteDataInTimeRange(sinceMs * 1000, Date.now() * 1000, true, flags, resolve);
      } else {
        Services.clearData.deleteData(flags, resolve);
      }
    });
    return "true";
  },
};

// ⇧⌘N opens a private tab in Mosaic (Gecko's own private window is swallowed above).
if (IS_MAC) {
  FORWARDED_WITH_SHIFT.n = "newPrivateTab";
}

Object.assign(Agent, PrivacyAgent);

// ---- end privacy area -------------------------------------------------------

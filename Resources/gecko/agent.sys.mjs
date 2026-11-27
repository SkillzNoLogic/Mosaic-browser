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

/**
 * Gecko's own shortcuts for whole windows: close window, reopen closed window,
 * new private window, and (where it is a key binding rather than a system menu
 * item) quit. Mosaic shows exactly one Gecko window, as part of its own, so
 * these would close the engine under it or put a second, chromeless Gecko
 * window on the desktop. They are swallowed.
 */
const SWALLOWED_WITH_SHIFT = new Set(IS_MAC ? ["w", "n", "p"] : ["w", "n", "p", "q"]);

function onKeyDown(event) {
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

  /** Starts password support: registers the window actors that watch sign-in forms. */
  async passwordsStart() {
    const result = await passwords().start({
      push: event => pendingEvents.push(event),
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

  /** Puts a generated password into the new-password fields of tab `id`. Returns the fields filled. */
  async passwordsFillGenerated(id, origin, password) {
    const count = await passwords().fill(requireTab(id).browser, origin, "MosaicPasswords:fillGenerated", { password });
    return JSON.stringify(count);
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

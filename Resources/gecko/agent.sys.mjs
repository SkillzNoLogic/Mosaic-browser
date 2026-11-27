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
const FORWARDED_WITH_SHIFT = { e: "toggleEngine", b: "toggleBlocker" };
// Engine area: Mosaic's find bar replaces Firefox's (macOS app only).
if (IS_MAC) {
  Object.assign(FORWARDED, { f: "find", g: "findNext" });
  Object.assign(FORWARDED_WITH_SHIFT, { g: "findPrevious" });
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
      tabs.push({
        id: idOf(tab),
        url,
        title: tab.label ?? "",
        loading: tab.hasAttribute("busy"),
        canGoBack: !!browser.canGoBack,
        canGoForward: !!browser.canGoForward,
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
};

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
const FORWARDED_WITH_SHIFT = { e: "toggleEngine" };

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
      if (startupTabs.has(tab) || tab.closing) {
        continue;
      }
      const browser = tab.linkedBrowser;
      let url = "";
      try {
        url = browser.currentURI?.spec ?? "";
      } catch (_) {}
      tabs.push({
        id: idOf(tab),
        url,
        title: tab.label ?? "",
        loading: tab.hasAttribute("busy"),
        canGoBack: !!browser.canGoBack,
        canGoForward: !!browser.canGoForward,
        selected: tab.selected,
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

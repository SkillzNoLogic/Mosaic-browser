/* Mosaic passwords inside Gecko (area: passwords). Platform-neutral.
 *
 * Loaded by the agent (agent.sys.mjs: passwordsStart/passwordsFill/passwordsFillGenerated).
 * Registers the MosaicPasswords window actors, turns their messages into events on the
 * agent's queue, and carries fill requests from Mosaic to the right frames.
 *
 * Trust: the origin of every message is read here, from the sending document's principal.
 * Messages from cross-origin subframes are dropped. Filling only ever targets frames whose
 * origin is exactly the requested one, and only when the tab's top-level document has that
 * origin too. Passwords pass through in memory only; nothing here logs or stores them.
 */

const ACTOR = "MosaicPasswords";
const CONTENT_FILES = ["MosaicPasswordsChild.sys.mjs", "passwords-page.js"];

/** Fields the page script may set, with their types; anything else is dropped. */
const PAGE_FIELDS = {
  kind: "string",
  doc: "string",
  login: "boolean",
  signup: "boolean",
  username: "string",
  password: "string",
  mode: "string",
  confirmed: "boolean",
  count: "number",
};

/** Set by the agent: { push(event), tabIdFor(browser) -> number | null }. */
let hooks = null;
let started = null;

function resourceHandler() {
  return Services.io.getProtocolHandler("resource").QueryInterface(Ci.nsISubstitutingProtocolHandler);
}

/** The file behind a resource://mosaic/ URL. */
function agentFilePath(name) {
  const spec = resourceHandler().resolveURI(Services.io.newURI(`resource://mosaic/${name}`));
  return Services.io.newURI(spec).QueryInterface(Ci.nsIFileURL).file.path;
}

/**
 * Gecko's own password manager would show its own prompts (in a window whose UI is
 * hidden) and keep its own copy of passwords. Mosaic replaces it.
 */
function disableGeckoPasswordManager() {
  for (const name of ["signon.rememberSignons", "signon.autofillForms", "signon.generation.enabled",
                      "signon.formlessCapture.enabled", "signon.management.page.breach-alerts.enabled"]) {
    try {
      Services.prefs.setBoolPref(name, false);
    } catch (_) {}
  }
  try {
    Services.prefs.setStringPref("signon.firefoxRelay.feature", "disabled");
  } catch (_) {}
}

async function start() {
  disableGeckoPasswordManager();

  // Content processes can read the profile's chrome/ folder, not Mosaic's app bundle.
  const folder = PathUtils.join(PathUtils.profileDir, "chrome", "mosaic-passwords");
  await IOUtils.makeDirectory(folder, { ignoreExisting: true, createAncestors: true });
  for (const name of CONTENT_FILES) {
    await IOUtils.copy(agentFilePath(name), PathUtils.join(folder, name));
  }
  const directory = Cc["@mozilla.org/file/local;1"].createInstance(Ci.nsIFile);
  directory.initWithPath(folder);
  resourceHandler().setSubstitution("mosaic-content", Services.io.newFileURI(directory));

  try {
    ChromeUtils.unregisterWindowActor(ACTOR);
  } catch (_) {}
  ChromeUtils.registerWindowActor(ACTOR, {
    parent: { esModuleURI: "resource://mosaic/MosaicPasswordsParent.sys.mjs" },
    child: {
      esModuleURI: "resource://mosaic-content/MosaicPasswordsChild.sys.mjs",
      events: {
        DOMContentLoaded: {},
        pageshow: {},
        DOMFormHasPassword: {},
        DOMInputPasswordAdded: {},
      },
    },
    allFrames: true,
    matches: ["http://*/*", "https://*/*"],
    messageManagerGroups: ["browsers"],
    // The parent side trusts nothing the content process says beyond the page fields.
    safeForUntrustedWebProcess: true,
  });

  // Pages that finished loading before the actor existed.
  const win = Services.wm.getMostRecentWindow("navigator:browser");
  for (const tab of win?.gBrowser?.tabs ?? []) {
    const top = tab.linkedBrowser?.browsingContext;
    for (const context of top?.getAllBrowsingContextsInSubtree() ?? []) {
      try {
        context.currentWindowGlobal?.getActor(ACTOR).sendAsyncMessage("MosaicPasswords:scan", {});
      } catch (_) {
        // Not an http(s) document.
      }
    }
  }
  return true;
}

function originOf(windowGlobal) {
  const principal = windowGlobal?.documentPrincipal;
  if (!principal?.isContentPrincipal || !(principal.schemeIs("http") || principal.schemeIs("https"))) {
    return null;
  }
  return principal.originNoSuffix;
}

export const MosaicPasswords = {
  /** Registers the actors (once per Gecko run). Resolves to true. */
  start(agentHooks) {
    hooks = agentHooks;
    if (!started) {
      started = start().catch(error => {
        started = null;
        throw error;
      });
    }
    return started;
  },

  /** A message from a MosaicPasswordsChild. */
  receive(actor, data) {
    if (!hooks || !data || typeof data !== "object") {
      return;
    }
    const context = actor.browsingContext;
    const origin = originOf(actor.manager);
    const topOrigin = originOf(context?.top?.currentWindowGlobal);
    if (!origin || !topOrigin) {
      return;
    }
    const frame = !context.parent ? "main" : origin === topOrigin ? "same" : "cross";
    if (frame === "cross") {
      return;
    }
    const browser = context.top.embedderElement;
    const tab = browser ? hooks.tabIdFor(browser) : null;
    if (tab === null || tab === undefined) {
      return;
    }
    const event = { origin, frame, tab, private: !!context.usePrivateBrowsing };
    for (const [key, type] of Object.entries(PAGE_FIELDS)) {
      if (typeof data[key] === type) {
        event[key] = data[key];
      }
    }
    hooks.push({ type: "passwords", passwords: event });
  },

  /**
   * Sends a fill request (`name`: MosaicPasswords:fill or MosaicPasswords:fillGenerated)
   * to the frames of `browser` whose origin is exactly `origin`, stopping at the first that
   * fills something. Returns the number of fields filled.
   */
  async fill(browser, origin, name, data) {
    const top = browser?.browsingContext;
    if (!top || originOf(top.currentWindowGlobal) !== origin) {
      return 0;
    }
    for (const context of top.getAllBrowsingContextsInSubtree()) {
      const windowGlobal = context.currentWindowGlobal;
      if (originOf(windowGlobal) !== origin) {
        continue;
      }
      try {
        const count = await windowGlobal.getActor(ACTOR).sendQuery(name, data);
        if (count > 0) {
          return count;
        }
      } catch (_) {
        // The document went away meanwhile.
      }
    }
    return 0;
  },
};

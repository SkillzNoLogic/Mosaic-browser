/* Mosaic's password page script (area: passwords). Platform-neutral.
 *
 * Shared by both engines:
 *  - Chromium: Mosaic runs it in the top-level frame and same-origin subframes of every
 *    http(s) page, wrapped in a function that supplies `__mosaicSend` (see CEFPasswords.mm).
 *  - Gecko: MosaicPasswordsChild.sys.mjs loads it with loadSubScript and runs it against the
 *    page through Xray wrappers, so the page's own scripts cannot see or tamper with it.
 *
 * It finds sign-in and sign-up forms, reports their submission (plain submits, forms handled
 * by script, single-page apps), offers generated passwords on new-password fields, and fills
 * saved credentials when Mosaic asks. It never fills anything on its own and never logs values.
 *
 * Two globals: `MosaicPasswordHeuristics` (pure functions over field descriptions, unit-tested
 * from Swift through JavaScriptCore) and `createMosaicPasswordPage(window, send, options)`.
 */

var MosaicPasswordHeuristics = (function () {
  "use strict";

  const USERNAME_HINT = /user|login|log-in|e-?mail|account|identifier|member|customer|signin|sign-in|nickname|benutzer|usuario|utilisateur|courriel|correo/i;
  const NOT_USERNAME_HINT = /search|captcha|otp|one.?time|verification|\bcode\b|token|coupon|promo|zip|postal|city|street|address|first.?name|last.?name|given|family|phone|card|cvc|cvv|amount|query/i;
  const NEW_PASSWORD_HINT = /new|confirm|repeat|retype|re-?enter|again|verify|create|choose|sign-?up|regist/i;
  const CURRENT_PASSWORD_HINT = /current|existing|(^|[^a-z])old/i;
  const TEXT_TYPES = ["text", "email", "tel", ""];

  function autocompleteTokens(field) {
    return String(field.autocomplete || "").toLowerCase().split(/\s+/).filter(Boolean);
  }

  function hintText(field) {
    return [field.name, field.id, field.placeholder, field.label].filter(Boolean).join(" ");
  }

  /**
   * What an input is, from its description:
   *   { type, name, id, autocomplete, placeholder, label, visible }
   * Returns "hidden", "password", "current-password", "new-password", "username", "text"
   * (a text field that might hold a username) or "other".
   */
  function fieldKind(field) {
    const type = String(field.type || "text").toLowerCase();
    if (field.visible === false) {
      return "hidden";
    }
    const tokens = autocompleteTokens(field);
    const hints = hintText(field);
    if (type === "password") {
      if (tokens.includes("new-password")) return "new-password";
      if (tokens.includes("current-password")) return "current-password";
      if (CURRENT_PASSWORD_HINT.test(hints)) return "current-password";
      if (NEW_PASSWORD_HINT.test(hints)) return "new-password";
      return "password";
    }
    if (!TEXT_TYPES.includes(type)) {
      return "other";
    }
    if (tokens.includes("username") || tokens.includes("email")) return "username";
    if (tokens.includes("one-time-code") || NOT_USERNAME_HINT.test(hints)) return "other";
    if (type === "email" || USERNAME_HINT.test(hints)) return "username";
    return "text";
  }

  function isPasswordKind(kind) {
    return kind === "password" || kind === "new-password" || kind === "current-password";
  }

  /**
   * Classifies a group of inputs (a form, or the inputs outside any form) given in document
   * order. Descriptions may carry `value` (at submission time) or not (when filling).
   * Returns null when the group has no visible password field, otherwise:
   *   mode       "login" | "signup" | "change"
   *   username   index of the username field, or -1
   *   password   index of the password to save (login: the password; signup/change: the new one)
   *   current    index of the current password in a change form, or -1
   *   newFields  indexes of the new-password fields (new + confirmation)
   *   passwords  indexes of all visible password fields
   */
  function classify(fields) {
    const kinds = fields.map(fieldKind);
    const passwords = [];
    kinds.forEach((kind, index) => {
      if (isPasswordKind(kind)) passwords.push(index);
    });
    if (!passwords.length) {
      return null;
    }
    let mode = "login";
    let password = passwords[0];
    let current = -1;
    let newFields = [];
    const explicitCurrent = passwords.find(index => kinds[index] === "current-password");
    if (passwords.length === 1) {
      if (kinds[password] === "new-password") {
        mode = "signup";
        newFields = [password];
      } else if (kinds[password] === "current-password") {
        mode = "login";
      }
    } else if (passwords.length === 2) {
      const [first, second] = passwords;
      const firstValue = fields[first].value, secondValue = fields[second].value;
      const valuesKnown = !!(firstValue || secondValue);
      if (explicitCurrent !== undefined) {
        mode = "change";
        current = explicitCurrent;
        password = explicitCurrent === first ? second : first;
        newFields = [password];
      } else if (!valuesKnown || firstValue === secondValue) {
        // A password and its confirmation.
        mode = "signup";
        password = first;
        newFields = [first, second];
      } else {
        mode = "change";
        current = first;
        password = second;
        newFields = [second];
      }
    } else {
      // Current, new and confirmation.
      mode = "change";
      current = explicitCurrent !== undefined ? explicitCurrent : passwords[0];
      newFields = passwords.filter(index => index !== current);
      password = newFields[0];
    }

    let username = -1;
    const firstPassword = passwords[0];
    for (let i = firstPassword - 1; i >= 0 && username < 0; i--) {
      if (kinds[i] === "username") username = i;
    }
    for (let i = firstPassword - 1; i >= 0 && username < 0; i--) {
      if (kinds[i] === "text") username = i;
    }
    if (username < 0) {
      username = kinds.indexOf("username");
    }
    if (username < 0) {
      // Change-password forms often carry the username in a field hidden from view,
      // marked autocomplete="username" so password managers can find it.
      username = fields.findIndex((field, index) =>
        kinds[index] === "hidden" && autocompleteTokens(field).includes("username"));
    }
    return { mode, username, password, current, newFields, passwords };
  }

  /** The single username-like field of a group without a password (step one of a two-step sign-in). */
  function identifierField(fields) {
    const kinds = fields.map(fieldKind);
    if (kinds.some(isPasswordKind)) return -1;
    const candidates = [];
    kinds.forEach((kind, index) => {
      if (kind === "username") candidates.push(index);
    });
    const visibleText = kinds.filter(kind => kind === "text" || kind === "username").length;
    return candidates.length === 1 && visibleText === 1 ? candidates[0] : -1;
  }

  return { fieldKind, classify, identifierField };
})();

function createMosaicPasswordPage(win, send, options) {
  "use strict";
  const H = MosaicPasswordHeuristics;
  const doc = win.document;
  const settings = options || {};
  const documentID = Math.random().toString(36).slice(2) + Date.now().toString(36);
  const listeners = [];
  let attached = false;
  let lastState = "";
  let observer = null;
  let reportTimer = null;
  let watcher = null;
  let pendingSubmit = null;
  let lastClick = null;
  const sent = new Set();

  function post(message) {
    message.doc = documentID;
    try {
      send(message);
    } catch (_) {}
  }

  // ---- Reading the page ------------------------------------------------------

  function isVisible(el) {
    try {
      if (!el.isConnected || el.type === "hidden" || el.hidden) return false;
      const rect = el.getBoundingClientRect();
      if (rect.width < 2 || rect.height < 2) return false;
      const style = win.getComputedStyle(el);
      return style.visibility !== "hidden" && style.display !== "none" && style.opacity !== "0";
    } catch (_) {
      return false;
    }
  }

  function labelText(el) {
    let text = el.getAttribute("aria-label") || "";
    try {
      for (const label of el.labels || []) text += " " + label.textContent;
    } catch (_) {}
    return text.trim().slice(0, 120);
  }

  function describe(el, withValue) {
    return {
      type: String(el.type || "text").toLowerCase(),
      name: el.name || "",
      id: el.id || "",
      autocomplete: el.getAttribute("autocomplete") || "",
      placeholder: el.getAttribute("placeholder") || "",
      label: labelText(el),
      visible: isVisible(el),
      value: withValue ? el.value || "" : "",
    };
  }

  /** Inputs of a form, or (form = null) the inputs that belong to no form. */
  function inputsOf(form) {
    const all = form ? Array.from(form.elements) : Array.from(doc.querySelectorAll("input"));
    return all.filter(el => el.localName === "input" && (form || !el.form));
  }

  function analyse(form, withValues) {
    const inputs = inputsOf(form);
    const result = H.classify(inputs.map(el => describe(el, withValues)));
    if (!result) return null;
    result.inputs = inputs;
    result.form = form;
    return result;
  }

  /** Every group (form, or null for formless fields) with a password field. */
  function passwordGroups() {
    const groups = [];
    for (const field of doc.querySelectorAll("input[type=password]")) {
      const form = field.form || null;
      if (!groups.includes(form)) groups.push(form);
    }
    return groups;
  }

  function loginGroups() {
    return passwordGroups().map(form => analyse(form, false)).filter(Boolean);
  }

  // ---- Reporting the page's state ------------------------------------------

  /** Tells Mosaic whether the page shows a sign-in or sign-up form (always on the first call). */
  function report(force) {
    let login = false, signup = false;
    for (const group of loginGroups()) {
      if (group.mode === "login" || group.mode === "change") login = true;
      if (group.mode !== "login") signup = true;
    }
    const state = (login ? "L" : "") + (signup ? "S" : "");
    if (!force && state === lastState) return;
    lastState = state;
    post({ kind: "page", login, signup });
  }

  function scheduleReport() {
    if (reportTimer !== null) return;
    reportTimer = win.setTimeout(() => {
      reportTimer = null;
      report(false);
    }, 400);
  }

  // ---- Submissions ------------------------------------------------------------

  function snapshot(form) {
    const group = analyse(form, true);
    if (!group) return null;
    const field = group.inputs[group.password];
    const password = field.value || "";
    if (!password) return null;
    const username = group.username >= 0 ? String(group.inputs[group.username].value || "").trim() : "";
    return { mode: group.mode, username, password, field };
  }

  function sendSubmit(snap, confirmed) {
    const key = (confirmed ? "c" : "u") + snap.mode + "\n" + snap.username + "\n" + snap.password;
    if (sent.has(key)) return;
    sent.add(key);
    post({ kind: "submit", username: snap.username, password: snap.password, mode: snap.mode, confirmed });
  }

  function sendIdentifier(form) {
    const inputs = inputsOf(form);
    const index = H.identifierField(inputs.map(el => describe(el, false)));
    if (index < 0) return;
    const username = String(inputs[index].value || "").trim();
    if (!username || sent.has("id\n" + username)) return;
    sent.add("id\n" + username);
    post({ kind: "identifier", username });
  }

  function anyVisiblePassword() {
    for (const field of doc.querySelectorAll("input[type=password]")) {
      if (isVisible(field)) return true;
    }
    return false;
  }

  /**
   * A sign-in the page handles itself (script-driven submit, single-page app): it counts as
   * done when the address changes or no password field is left on the page. A sign-in that
   * fails normally leaves the form in place, and then no confirmation is sent.
   *
   * The candidate is reported at once, unconfirmed, in case the page navigates away instead:
   * Mosaic then judges it from the next page, as for a plain submit. (There is deliberately no
   * pagehide/unload listener to do this later: in Chromium such a listener disables fast tab
   * shutdown, so every page would close slowly.)
   */
  function watch(snap) {
    stopWatching();
    sendSubmit(snap, false);
    const startAddress = win.location.href;
    let ticks = 0;
    watcher = {
      snap,
      timer: win.setInterval(() => {
        ticks += 1;
        const fieldGone = !snap.field.isConnected || !isVisible(snap.field);
        if (win.location.href !== startAddress || (fieldGone && !anyVisiblePassword())) {
          stopWatching();
          sendSubmit(snap, true);
        } else if (ticks > 60) {
          stopWatching();
        }
      }, 250),
    };
  }

  function stopWatching() {
    if (watcher) {
      win.clearInterval(watcher.timer);
      watcher = null;
    }
  }

  function decideSubmit(prevented) {
    const pending = pendingSubmit;
    pendingSubmit = null;
    if (!pending) return;
    if (prevented) {
      watch(pending.snap);
    } else {
      // A real submission: the page is about to navigate. Mosaic decides once the next
      // page has loaded (a sign-in form again on the same site means it failed).
      sendSubmit(pending.snap, false);
    }
  }

  function onSubmitCapture(event) {
    const form = event.target;
    if (!form || form.localName !== "form") return;
    const snap = snapshot(form);
    if (!snap) {
      sendIdentifier(form);
      return;
    }
    pendingSubmit = { snap, event };
    // Normally decided in the bubbling listener, after the page's own handlers.
    win.setTimeout(() => {
      if (pendingSubmit && pendingSubmit.event === event) decideSubmit(event.defaultPrevented);
    }, 0);
  }

  function onSubmitBubble(event) {
    if (pendingSubmit && pendingSubmit.event === event) decideSubmit(event.defaultPrevented);
  }

  function groupForElement(el) {
    if (!el) return undefined;
    if (el.form) return el.form;
    const form = el.closest ? el.closest("form") : null;
    return form || null;
  }

  /** A click on a button or Enter in a field, which may start a sign-in without a submit event. */
  function possibleSubmission(el) {
    const form = groupForElement(el);
    if (form === undefined) return;
    const snap = snapshot(form);
    if (!snap) {
      if (form) return; // a real form reports its identifier through its submit event
      return;
    }
    const marker = {};
    lastClick = marker;
    // If a submit event follows it handles this; otherwise the page does it by script.
    win.setTimeout(() => {
      if (lastClick !== marker) return;
      lastClick = null;
      if (!sent.has("c" + snap.mode + "\n" + snap.username + "\n" + snap.password) && !pendingSubmit) watch(snap);
    }, 60);
  }

  function onClick(event) {
    const target = event.target;
    if (!target || !target.closest) return;
    const button = target.closest("button, input[type=submit], input[type=button], input[type=image], [role=button], a");
    if (button) possibleSubmission(button);
  }

  function onKeyDown(event) {
    const target = event.target;
    // Only a real key press: a page must not be able to ask for a fill by synthesising one.
    if (settings.fillShortcut && event.isTrusted && event.key === "\\" && !event.altKey && !event.shiftKey &&
        (settings.isMac ? event.metaKey && !event.ctrlKey : event.ctrlKey && !event.metaKey)) {
      event.preventDefault();
      event.stopPropagation();
      post({ kind: "fillShortcut" });
      return;
    }
    if (event.key === "Enter" && target && target.localName === "input") possibleSubmission(target);
  }

  function onFocusIn(event) {
    const field = event.target;
    if (!field || field.localName !== "input") return;
    if (field.type !== "password") return;
    const group = analyse(field.form || null, false);
    scheduleReport();
    if (!group || field.value) return;
    if (group.newFields.includes(group.inputs.indexOf(field))) post({ kind: "generate" });
  }

  // ---- Filling ----------------------------------------------------------------

  function setValue(el, value) {
    // The prototype's setter, not `el.value =`: frameworks such as React shadow `value` on the
    // element to track changes, and would otherwise not notice the new value.
    const descriptor = Object.getOwnPropertyDescriptor(win.HTMLInputElement.prototype, "value");
    if (descriptor && descriptor.set) {
      descriptor.set.call(el, value);
    } else {
      el.value = value;
    }
    el.dispatchEvent(new win.Event("input", { bubbles: true }));
    el.dispatchEvent(new win.Event("change", { bubbles: true }));
  }

  /** Fills a saved credential into the page's sign-in form. Returns the number of fields filled. */
  function fill(username, password) {
    const groups = loginGroups();
    const group = groups.find(g => g.mode === "login") || groups.find(g => g.mode === "change");
    if (!group) {
      // Step one of a two-step sign-in: only a username field.
      if (!username) return 0;
      for (const form of [...doc.querySelectorAll("form"), null]) {
        const inputs = inputsOf(form);
        const index = H.identifierField(inputs.map(el => describe(el, false)));
        if (index >= 0) {
          setValue(inputs[index], username);
          return 1;
        }
      }
      return 0;
    }
    let count = 0;
    if (username && group.username >= 0 && isVisible(group.inputs[group.username])) {
      setValue(group.inputs[group.username], username);
      count += 1;
    }
    const target = group.mode === "change" ? group.current : group.password;
    if (target >= 0) {
      setValue(group.inputs[target], password);
      count += 1;
    }
    return count;
  }

  /** Puts a generated password into the focused new-password field and its confirmation. */
  function fillGenerated(password) {
    const active = doc.activeElement;
    let group = null;
    if (active && active.localName === "input" && active.type === "password") {
      group = analyse(active.form || null, false);
    }
    if (!group || !group.newFields.length) {
      group = loginGroups().find(g => g.newFields.length) || null;
    }
    if (!group) return 0;
    for (const index of group.newFields) setValue(group.inputs[index], password);
    return group.newFields.length;
  }

  // ---- Lifecycle ----------------------------------------------------------------

  function listen(target, type, handler, capture) {
    target.addEventListener(type, handler, capture);
    listeners.push([target, type, handler, capture]);
  }

  function attach() {
    if (attached) return;
    attached = true;
    listen(win, "submit", onSubmitCapture, true);
    listen(win, "submit", onSubmitBubble, false);
    listen(win, "click", onClick, true);
    listen(win, "keydown", onKeyDown, true);
    listen(win, "focusin", onFocusIn, true);
    try {
      observer = new win.MutationObserver(scheduleReport);
      observer.observe(doc, { childList: true, subtree: true });
    } catch (_) {
      observer = null;
    }
    if (doc.readyState === "loading") {
      listen(win, "DOMContentLoaded", () => report(true), false);
    } else {
      report(true);
    }
  }

  function detach() {
    stopWatching();
    for (const [target, type, handler, capture] of listeners) {
      try {
        target.removeEventListener(type, handler, capture);
      } catch (_) {}
    }
    listeners.length = 0;
    if (observer) observer.disconnect();
    if (reportTimer !== null) win.clearTimeout(reportTimer);
    attached = false;
  }

  return { attach, detach, report, fill, fillGenerated };
}

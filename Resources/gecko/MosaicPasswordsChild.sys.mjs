/* Mosaic passwords: the content-process half of the MosaicPasswords window actor
 * (area: passwords). Platform-neutral.
 *
 * Runs in Gecko's content processes, one actor per document of an http(s) page. It loads
 * the shared page script (passwords-page.js) and runs it against the page through Xray
 * wrappers. Messages go to MosaicPasswordsParent, which adds the origin from the document's
 * principal; nothing the page says about itself is trusted there.
 *
 * Content processes are sandboxed and may only read Gecko's own files and the profile's
 * chrome/ and extensions/ folders, so passwords.sys.mjs copies this module and the page
 * script into <profile>/chrome/mosaic-passwords/ (resource://mosaic-content/) at start.
 */

const scope = {};
Services.scriptloader.loadSubScript("resource://mosaic-content/passwords-page.js", scope);

const IS_MAC = Services.appinfo.OS === "Darwin";

export class MosaicPasswordsChild extends JSWindowActorChild {
  #page = null;

  #start() {
    if (this.#page) {
      return this.#page;
    }
    const win = this.contentWindow;
    const protocol = win?.location?.protocol;
    if (protocol !== "http:" && protocol !== "https:") {
      return null;
    }
    this.#page = scope.createMosaicPasswordPage(
      win,
      message => {
        try {
          this.sendAsyncMessage("MosaicPasswords:message", message);
        } catch (_) {
          // The actor is going away with its document.
        }
      },
      { fillShortcut: true, isMac: IS_MAC }
    );
    this.#page.attach();
    return this.#page;
  }

  handleEvent(event) {
    switch (event.type) {
      case "DOMContentLoaded":
      case "pageshow":
        this.#start();
        break;
      case "DOMFormHasPassword":
      case "DOMInputPasswordAdded":
        this.#start()?.report(false);
        break;
    }
  }

  receiveMessage(message) {
    const page = this.#start();
    if (!page) {
      return 0;
    }
    switch (message.name) {
      case "MosaicPasswords:fill":
        return page.fill(String(message.data.username ?? ""), String(message.data.password ?? ""));
      case "MosaicPasswords:fillGenerated":
        return page.fillGenerated(String(message.data.password ?? ""));
      case "MosaicPasswords:scan":
        page.report(true);
        return 0;
    }
    return 0;
  }

  didDestroy() {
    this.#page?.detach();
    this.#page = null;
  }
}

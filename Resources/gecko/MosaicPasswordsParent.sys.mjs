/* Mosaic passwords: the parent-process half of the MosaicPasswords window actor
 * (area: passwords). Platform-neutral. Hands every message to passwords.sys.mjs, which
 * checks where it came from and queues it for Mosaic.
 */

import { MosaicPasswords } from "resource://mosaic/passwords.sys.mjs";

export class MosaicPasswordsParent extends JSWindowActorParent {
  receiveMessage(message) {
    if (message.name === "MosaicPasswords:message") {
      MosaicPasswords.receive(this, message.data);
    }
  }
}

// Password save and fill for Chromium tabs (area: passwords). Implemented in CEFPasswords.mm.
//
// Mosaic has no renderer-process code, so page scripts are run with
// CefFrame::ExecuteJavaScript and talk back through console messages that carry a
// per-frame secret token. The token is bound, on the browser side, to the frame and the
// origin it was handed to; a message is accepted only while that frame still shows that
// origin, and it is reported with the origin the bridge knows, never one the page claims.
// Messages carrying the prefix are never passed to Chromium's log.

#import <AppKit/AppKit.h>

#import "CEFBridge.h"

NS_ASSUME_NONNULL_BEGIN

/// Where a password message came from, relative to the tab's top-level document.
typedef NS_ENUM(NSInteger, MCEFPasswordFrame) {
    MCEFPasswordFrameMain = 0,
    MCEFPasswordFrameSameOrigin = 1,
};

@interface MosaicCEF (Passwords)

/// The shared page script (Resources/gecko/passwords-page.js). Every password script runs
/// with it in scope. Set once, before any browser exists.
@property(class, copy, nullable) NSString *passwordPageScript;

/// JavaScript run in the top-level frame and in same-origin subframes of every http(s)
/// page after it loads (cross-origin subframes are left alone). `__mosaicSend(object)`
/// reports to `passwordMessageHandler`. Nil: nothing is injected.
@property(class, copy, nullable) NSString *passwordLoadScript;

/// Receives password messages: the view, the frame's origin as the bridge established it
/// (e.g. "https://example.com"), where the frame is, and the page script's JSON object.
/// Main thread. Messages from cross-origin subframes are dropped before this.
@property(class, copy, nullable) void (^passwordMessageHandler)
    (MCEFBrowserView *view, NSString *origin, MCEFPasswordFrame frame, NSString *json);

@end

@interface MCEFBrowserView (Passwords)

/// Runs `body` (with the page script in scope) in every frame whose origin is exactly
/// `origin`, provided the top-level document has that origin too. Returns the number of
/// frames it was sent to; 0 when the page is not on `origin`.
- (NSInteger)runPasswordScript:(NSString *)body inFramesWithOrigin:(NSString *)origin;

@end

NS_ASSUME_NONNULL_END

// Objective-C face of Mosaic's Chromium engine (Chromium Embedded Framework).
// Everything C++ stays behind this header so Swift can import it directly.

#import <AppKit/AppKit.h>

NS_ASSUME_NONNULL_BEGIN

/// The application class Mosaic must run as. Chromium needs to know when the
/// app is in the middle of dispatching an event, which only an NSApplication
/// subclass can tell it. Create it with `MosaicApplication.shared` before
/// anything else touches `NSApp`.
@interface MosaicApplication : NSApplication
@end

/// Process-wide Chromium lifecycle. Main thread only.
@interface MosaicCEF : NSObject

/// True when the Chromium framework is present inside the app bundle.
@property(class, readonly) BOOL isBundled;

/// True once `start` has succeeded.
@property(class, readonly) BOOL isRunning;

/// Version of the embedded Chromium, e.g. "158.0.8127.89".
@property(class, readonly, copy) NSString *chromiumVersion;

/// Called once when the user quits, before browsers are torn down.
@property(class, copy, nullable) void (^willQuitHandler)(void);

/// Content blocking for Chromium: asked about every sub-resource request a page
/// makes (never the page's own top-level document). Return YES to block it.
///
/// `documentURL` is the page the request belongs to; `resourceType` is a
/// webRequest type name ("script", "image", "xmlhttprequest", "sub_frame", …).
///
/// The block is called on Chromium's network thread, for every request, so it
/// must be thread-safe and fast. Set it once, before any browser exists.
@property(class, copy, nullable) BOOL (^requestFilter)
    (NSString *url, NSString *documentURL, NSString *resourceType);

/// Loads the framework and starts Chromium. `dataDirectory` holds the profile
/// (cookies, cache, storage). Returns NO if Chromium could not start, in which
/// case the app can still run with its other engine.
+ (BOOL)startWithArgc:(int)argc
                 argv:(char *_Nullable *_Nonnull)argv
        dataDirectory:(NSString *)dataDirectory;

/// Runs the app's event loop until `quit`. Use instead of `-[NSApplication run]`.
+ (void)runEventLoop;

/// Closes every browser, then ends `runEventLoop`.
+ (void)quit;

/// Releases Chromium. Call after `runEventLoop` returns.
+ (void)shutdown;

@end

@class MCEFBrowserView;

/// Navigation state changes of one Chromium browser. Called on the main thread.
@protocol MCEFBrowserDelegate <NSObject>
- (void)browserView:(MCEFBrowserView *)view didChangeTitle:(NSString *)title;
- (void)browserView:(MCEFBrowserView *)view didChangeURL:(NSString *)url;
- (void)browserView:(MCEFBrowserView *)view
    didChangeLoading:(BOOL)isLoading
           canGoBack:(BOOL)canGoBack
        canGoForward:(BOOL)canGoForward;
/// The page asked for a new window or tab (target=_blank, window.open, ⌘-click).
- (void)browserView:(MCEFBrowserView *)view wantsNewTabWithURL:(NSString *)url;
/// The browser started loading a new top-level document.
- (void)browserViewDidStartDocumentLoad:(MCEFBrowserView *)view;
/// `requestFilter` blocked a request made by this browser's page.
- (void)browserView:(MCEFBrowserView *)view didBlockRequest:(NSString *)url;
@end

/// A view that hosts one Chromium browser. The browser is created the first
/// time the view is placed in a window.
@interface MCEFBrowserView : NSView

@property(nonatomic, weak, nullable) id<MCEFBrowserDelegate> delegate;

- (instancetype)initWithURL:(NSString *)url;

- (void)loadURL:(NSString *)url;
- (void)goBack;
- (void)goForward;
- (void)reload;
- (void)stopLoading;
/// Gives keyboard focus to the page.
- (void)focusContent;
/// Destroys the browser. The view is inert afterwards.
- (void)closeBrowser;

@end

// MARK: - Page features (engine area)
//
// Find in page, zoom, printing, developer tools, audio, the DevTools protocol, permission
// prompts, pop-up blocking and page full screen. Implemented in CEFBridge+Engine.mm.
// Every method is main-thread only and safe to call before the browser exists: settings
// made early (zoom, mute) are applied once it does, and requests that need a page fail
// through their completion.

/// What a page asks permission for. A request can combine several (camera + microphone).
typedef NS_OPTIONS(NSUInteger, MCEFPermissionKinds) {
    MCEFPermissionCamera = 1 << 0,
    MCEFPermissionMicrophone = 1 << 1,
    MCEFPermissionGeolocation = 1 << 2,
    MCEFPermissionNotifications = 1 << 3,
    MCEFPermissionClipboard = 1 << 4,
    /// Screen or tab sharing (getDisplayMedia).
    MCEFPermissionScreenCapture = 1 << 5,
    /// Anything else; `otherKinds` names it.
    MCEFPermissionOther = 1 << 6,
};

/// One pending permission request. Answer it exactly once; a request that is released
/// unanswered is denied, so a page never waits forever.
@interface MCEFPermissionRequest : NSObject
/// The requesting origin, e.g. "https://meet.example".
@property(nonatomic, readonly, copy) NSString *origin;
@property(nonatomic, readonly) MCEFPermissionKinds kinds;
/// Names of requested permissions outside `kinds` ("midi-sysex", "storage-access",
/// "multiple-downloads", "window-management", "local-fonts", "idle-detection",
/// "local-network", …).
@property(nonatomic, readonly, copy) NSArray<NSString *> *otherKinds;
/// Unique per request.
@property(nonatomic, readonly) uint64_t identifier;
@property(nonatomic, readonly) BOOL isAnswered;
- (instancetype)init NS_UNAVAILABLE;
/// Grants everything requested.
- (void)allow;
/// Refuses (the page is told it was denied).
- (void)deny;
/// Closes the request without a decision (the page sees a dismissal; it may ask again).
- (void)dismiss;
@end

typedef NS_ENUM(NSInteger, MCEFDialogKind) {
    MCEFDialogKindAlert,
    MCEFDialogKindConfirm,
    MCEFDialogKindPrompt,
    /// "Leave this page?" (onbeforeunload).
    MCEFDialogKindBeforeUnload,
};

/// A JavaScript dialog (alert / confirm / prompt / beforeunload) the page is waiting on.
/// Finish it exactly once; one released unfinished is cancelled.
@interface MCEFJavaScriptDialog : NSObject
@property(nonatomic, readonly) MCEFDialogKind kind;
@property(nonatomic, readonly, copy) NSString *message;
/// Prompt dialogs: the text field's initial value.
@property(nonatomic, readonly, copy) NSString *defaultText;
/// The page's origin, formatted for display ("example.com"); empty for data: pages.
@property(nonatomic, readonly, copy) NSString *origin;
@property(nonatomic, readonly) BOOL isFinished;
- (instancetype)init NS_UNAVAILABLE;
/// OK / Leave (`accepted` YES) or Cancel / Stay; `text` is the prompt's answer.
- (void)finishAccepted:(BOOL)accepted text:(nullable NSString *)text;
@end

/// Page events beyond navigation. Every method is optional and called on the main thread.
@protocol MCEFPageDelegate <NSObject>
@optional
/// The browser now exists; settings made earlier have been applied.
- (void)browserViewDidCreateBrowser:(MCEFBrowserView *)view;
/// Progress of `findText:…`. `activeMatch` is 1-based (0 while unknown); `finalUpdate` is
/// YES for the last report of a search.
- (void)browserView:(MCEFBrowserView *)view
    didUpdateFindMatches:(NSInteger)count
             activeMatch:(NSInteger)activeMatch
             finalUpdate:(BOOL)finalUpdate;
/// The page started producing sound. Chromium (Alloy) reports no matching "stopped" event,
/// so the app has to check (Mosaic asks the page over the DevTools protocol).
- (void)browserViewDidStartAudio:(MCEFBrowserView *)view;
/// The page's camera / microphone use changed.
- (void)browserView:(MCEFBrowserView *)view didChangeMediaAccessVideo:(BOOL)video audio:(BOOL)audio;
/// The page asks for a permission. Answer through the request, now or later.
- (void)browserView:(MCEFBrowserView *)view requestsPermission:(MCEFPermissionRequest *)request;
/// A request went away unanswered (the page navigated or was closed); remove its prompt.
- (void)browserView:(MCEFBrowserView *)view didCancelPermissionRequest:(MCEFPermissionRequest *)request;
/// A pop-up opened without a user gesture was blocked. Implementing this method turns
/// blocking on; without it every pop-up becomes a new tab as before.
- (void)browserView:(MCEFBrowserView *)view didBlockPopupWithURL:(NSString *)url;
/// The page entered or left full screen (the JavaScript Fullscreen API). With Alloy-style
/// browsers the app makes the view fill the screen; Esc leaves (handled by the bridge).
- (void)browserView:(MCEFBrowserView *)view didChangeFullscreen:(BOOL)fullscreen;
/// The page opened a JavaScript dialog. Implementing this replaces Chromium's own (an
/// app-wide alert titled with the raw page URL) with the app's.
- (void)browserView:(MCEFBrowserView *)view runJavaScriptDialog:(MCEFJavaScriptDialog *)dialog;
/// Pending dialogs were dropped (navigation, reload): take down their UI.
- (void)browserViewDidResetJavaScriptDialogs:(MCEFBrowserView *)view;
@end

/// Chromium-wide settings an origin can be given, mirrored from Mosaic's site permissions.
typedef NS_ENUM(NSInteger, MCEFContentSetting) {
    MCEFContentSettingGeolocation,
    MCEFContentSettingNotifications,
    MCEFContentSettingClipboard,
    MCEFContentSettingCamera,
    MCEFContentSettingMicrophone,
};

typedef NS_ENUM(NSInteger, MCEFContentSettingValue) {
    /// Chromium's default for the setting (ask).
    MCEFContentSettingValueDefault,
    MCEFContentSettingValueAllow,
    MCEFContentSettingValueBlock,
};

@interface MosaicCEF (PageFeatures)
/// Receives page events from every browser view that has no `pageDelegate` of its own.
@property(class, weak, nullable) id<MCEFPageDelegate> pageDelegate;
/// Hands pop-up blocking to the page delegate. Chromium's own blocker (part of CEF 158 even
/// for Alloy browsers) would otherwise swallow gesture-less pop-ups before
/// `browserView:didBlockPopupWithURL:` could report them; this lets every pop-up through
/// to the bridge, which blocks those without a user gesture and reports them.
+ (void)routePopupBlockingToPageDelegate;
/// Sets what Chromium remembers for `origin`; Default makes it ask again.
+ (void)setContentSetting:(MCEFContentSetting)setting
                    value:(MCEFContentSettingValue)value
                forOrigin:(NSString *)origin;
@end

@interface MCEFBrowserView (PageFeatures)
/// Overrides `MosaicCEF.pageDelegate` for this view.
@property(nonatomic, weak, nullable) id<MCEFPageDelegate> pageDelegate;
/// True once the browser exists (and until it is closed).
@property(nonatomic, readonly) BOOL hasBrowser;

/// Finds `text`. Start with `findNext` NO; repeat with YES to move between matches. A new
/// text or case setting restarts the search. Results go to the page delegate.
- (void)findText:(NSString *)text forward:(BOOL)forward matchCase:(BOOL)matchCase findNext:(BOOL)findNext;
/// Ends the search; `clearSelection` NO leaves the last match selected.
- (void)stopFindingAndClearSelection:(BOOL)clearSelection;

/// Chromium zoom level: 0 is 100 %, each step is a factor of 1.2 (see `PageZoom`).
@property(nonatomic) double zoomLevel;

/// Shows the system print dialog for the page.
- (void)print NS_SWIFT_NAME(printPage());
/// Saves the page as a PDF at `path` (paper size from the system's default printer setup).
- (void)printToPDF:(NSString *)path completion:(void (^)(BOOL ok))completion
    NS_SWIFT_NAME(printToPDF(_:completion:));

/// Opens (or focuses) Chromium's developer tools in their own window.
- (void)showDevTools;
- (void)closeDevTools;
@property(nonatomic, readonly) BOOL hasDevTools;

@property(nonatomic) BOOL audioMuted;

/// Runs a DevTools protocol method (https://chromedevtools.github.io/devtools-protocol/)
/// and delivers its result dictionary, or an error message.
- (void)executeDevToolsMethod:(NSString *)method
                       params:(nullable NSDictionary<NSString *, id> *)params
                   completion:(void (^)(NSDictionary<NSString *, id> *_Nullable result,
                                        NSString *_Nullable error))completion;

/// Chromium's own view inside this one (nil without a browser). Page full screen moves it
/// into a screen-sized window and back; the browser view itself stays where the UI put it.
@property(nonatomic, readonly, nullable) NSView *pageContentView;

/// Whether the page is in (JavaScript API) full screen, and a way out of it.
@property(nonatomic, readonly) BOOL isFullscreen;
- (void)exitFullscreen;
@end

NS_ASSUME_NONNULL_END

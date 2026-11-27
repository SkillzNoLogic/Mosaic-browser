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

// MARK: Page scripts (productivity area, for reader mode; usable by any feature)
// Implemented in MCEFBrowserView+Script.mm over CEF's in-process DevTools protocol.
@interface MCEFBrowserView (MosaicScript)

/// True once the browser exists (scripts can run).
@property(nonatomic, readonly) BOOL hasPageScriptSupport;

/// Evaluates `script` in the page's main frame, awaiting it if it returns a promise.
/// `value` is the JSON-compatible result (NSString, NSNumber, NSArray, NSDictionary, NSNull).
- (void)evaluatePageScript:(NSString *)script
                completion:(void (^)(id _Nullable value, NSString *_Nullable error))completion;

/// Installs a function `window[name](string)` in this browser's pages (now and after every
/// navigation); each call delivers its string to `handler`. Pages can call it too, so treat
/// payloads as untrusted input.
- (void)addPageMessageHandlerNamed:(NSString *)name handler:(void (^)(NSString *payload))handler;

/// Removes the page function and handler added with `addPageMessageHandlerNamed:handler:`.
- (void)removePageMessageHandlerNamed:(NSString *)name;

/// Drops every page-script handler and pending call (done by `closeBrowser`): a DevTools
/// observer left registered would hold up closing the browser.
- (void)releasePageScripts;

/// Calls any DevTools protocol method (e.g. "Page.captureScreenshot") on this browser.
/// `result` is the method's result dictionary; `error` is set when it failed.
- (void)callDevToolsMethod:(NSString *)method
                    params:(nullable NSDictionary<NSString *, id> *)params
                completion:(void (^)(NSDictionary *_Nullable result, NSString *_Nullable error))completion;

@end

NS_ASSUME_NONNULL_END

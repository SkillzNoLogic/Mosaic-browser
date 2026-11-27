// Privacy features of Mosaic's Chromium engine (owner: privacy area).
// Imported by CEFBridge.h; implemented in CEFPrivacy.mm. Main thread only.

#import "CEFBridge.h"

NS_ASSUME_NONNULL_BEGIN

@interface MosaicPrivacyCEF : NSObject

/// Cosmetic filtering. Asked each time a frame commits a new document; return
/// JavaScript to run in that frame (Mosaic's style injection), or nil.
/// `pageURL` is the top-level page's address, `isMainFrame` whether the frame
/// is that page.
@property(class, copy, nullable) NSString *_Nullable (^frameScriptProvider)
    (NSString *frameURL, NSString *pageURL, BOOL isMainFrame);

/// HTTPS-only mode and tracking-parameter removal. Asked before every
/// top-level navigation (typed, link, redirect). Return the address to load
/// instead, or nil to let the navigation proceed. `allowInsecure` is YES when
/// the user chose "Continue to HTTP Site" on Mosaic's HTTPS-only page.
@property(class, copy, nullable) NSString *_Nullable (^navigationRewriter)
    (NSString *url, BOOL isRedirect, BOOL allowInsecure);

/// Told the host whose plain-HTTP page the user chose to open from the
/// HTTPS-only page, so later visits this session go straight through.
@property(class, copy, nullable) void (^insecureContinueHandler)(NSString *host);

/// Sets profile preferences (Chromium names, e.g. "profile.cookie_controls_mode")
/// on the default profile and on the private one, if it exists; remembered and
/// applied to a private profile created later. Values: NSNumber (bool, int,
/// double), NSString, or NSArray of those. Returns name -> "" on success or the
/// error Chromium reported.
+ (NSDictionary<NSString *, NSString *> *)setProfilePreferences:(NSDictionary<NSString *, id> *)preferences;

/// Sets browser-wide ("local state") preferences, e.g. "dns_over_https.mode".
+ (NSDictionary<NSString *, NSString *> *)setGlobalPreferences:(NSDictionary<NSString *, id> *)preferences;

/// A preference's current value, or nil if Chromium has no such preference.
+ (nullable id)profilePreference:(NSString *)name;
+ (nullable id)globalPreference:(NSString *)name;

/// Makes cookies of the default profile last until quit (YES) or as long as
/// sites ask (NO), through the default cookie content setting.
+ (void)setCookiesSessionOnly:(BOOL)sessionOnly;

/// Every cookie of the default (non-private) profile, as dictionaries with
/// keys domain, name, path, secure, httpOnly, creation (seconds since 1970).
/// The completion runs on the main thread.
+ (void)visitCookies:(void (^)(NSArray<NSDictionary<NSString *, id> *> *cookies))completion;

/// Deletes cookies of the default profile whose domain is `domain` or one of
/// its subdomains (every cookie when nil) that were created at or after
/// `since` (whenever when nil). The completion gets the number deleted.
+ (void)deleteCookiesForDomain:(nullable NSString *)domain
                         since:(nullable NSDate *)since
                    completion:(void (^)(NSInteger deleted))completion;

/// Clears the default profile's HTTP cache.
+ (void)clearCache:(void (^)(void))completion;

/// Clears every kind of storage (local storage, IndexedDB, cache storage,
/// service workers, cookies) of these origins ("https://example.com") through
/// the DevTools protocol. That needs a live Chromium browser; if no tab has
/// one, a hidden one is created for the purpose and closed afterwards.
+ (void)clearStorageForOrigins:(NSArray<NSString *> *)origins completion:(void (^)(BOOL done))completion;

/// Live private browsers. The in-memory private profile exists only while
/// this is above zero; when the last private browser closes it is released
/// and its cookies, storage and cache are gone.
@property(class, readonly) NSInteger privateBrowserCount;
@property(class, readonly) BOOL hasPrivateProfile;

/// Test hook: the cookies of the private profile (empty when there is none).
+ (void)visitPrivateCookies:(void (^)(NSArray<NSDictionary<NSString *, id> *> *cookies))completion;

@end

@interface MCEFBrowserView (MosaicPrivacy)
/// A private browser: its cookies, storage and cache are kept in memory, in a
/// profile shared by private views only and discarded when the last one
/// closes. Nothing is written to disk. Set before the view is placed in a
/// window (the browser is created then).
@property(nonatomic, setter=setPrivate:) BOOL isPrivate;
@end

NS_ASSUME_NONNULL_END

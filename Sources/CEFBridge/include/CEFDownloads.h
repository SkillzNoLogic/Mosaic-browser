// Downloads in Mosaic's Chromium engine (downloads area). Implemented in CEFDownloads.mm;
// CEFBridge.mm only hands CEF the handler (see the "downloads area" hooks there).

#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

typedef NS_ENUM(NSInteger, MCEFDownloadState) {
  MCEFDownloadStateInProgress,
  MCEFDownloadStatePaused,
  MCEFDownloadStateComplete,
  MCEFDownloadStateCancelled,
  /// Stopped by an error; `interruptReason` says which. Chromium can often resume it.
  MCEFDownloadStateInterrupted,
};

/// A snapshot of one Chromium download.
@interface MCEFDownload : NSObject
/// Chromium's ID for the download, unique while Mosaic runs.
@property(nonatomic, readonly) uint32_t identifier;
@property(nonatomic, readonly, copy) NSString *url;
@property(nonatomic, readonly, copy) NSString *originalURL;
/// The name Chromium suggests (from Content-Disposition or the URL), not yet sanitised.
@property(nonatomic, readonly, copy) NSString *suggestedName;
/// Where the file is being written; empty until a path was chosen.
@property(nonatomic, readonly, copy) NSString *fullPath;
@property(nonatomic, readonly, copy) NSString *mimeType;
/// The page the download started from, when known.
@property(nonatomic, readonly, copy) NSString *pageURL;
/// -1 when unknown.
@property(nonatomic, readonly) int64_t totalBytes;
@property(nonatomic, readonly) int64_t receivedBytes;
/// Bytes per second.
@property(nonatomic, readonly) int64_t speed;
@property(nonatomic, readonly) MCEFDownloadState state;
/// Chromium's cef_download_interrupt_reason_t; 0 when not interrupted.
@property(nonatomic, readonly) int interruptReason;
/// A short English description of `interruptReason`.
@property(nonatomic, readonly, copy) NSString *interruptDescription;
@end

/// Receives Chromium's downloads. Called on the main thread.
@protocol MCEFDownloadDelegate <NSObject>
/// A download is about to be written to disk. Call `decide` exactly once, on the main
/// thread, now or later, with the full path to save to, or nil to cancel the download
/// (nothing is written then).
- (void)chromiumDownloadWillStart:(MCEFDownload *)download
                           decide:(void (^)(NSString *_Nullable path))decide;
/// Progress or state changed.
- (void)chromiumDownloadDidUpdate:(MCEFDownload *)download;
/// The user chose "Download Linked File with Free Download Manager" in a page's menu.
- (void)chromiumContextMenuSendLinkToFDM:(NSString *)linkURL pageURL:(NSString *)pageURL;
@end

/// Process-wide control of Chromium downloads. Main thread only.
@interface MosaicCEFDownloads : NSObject

@property(class, nonatomic, weak, nullable) id<MCEFDownloadDelegate> delegate;

/// Adds "Download Linked File with Free Download Manager" to link context menus.
@property(class, nonatomic) BOOL showsFDMContextMenuItem;

/// The User-Agent Chromium sends (CEF uses Chrome's default), for handing a download to
/// another program.
@property(class, nonatomic, readonly, copy) NSString *userAgent;

+ (void)pauseDownload:(uint32_t)identifier;
+ (void)resumeDownload:(uint32_t)identifier;
+ (void)cancelDownload:(uint32_t)identifier;

/// True while Chromium still holds the download and can pause/resume/cancel it.
+ (BOOL)isControllable:(uint32_t)identifier;

/// Starts downloading `url` (it goes through the delegate like any other download).
/// Returns NO when no Chromium browser exists to start it from.
+ (BOOL)startDownloadWithURL:(NSString *)url;

/// The cookies Chromium would send to `url`, as a Cookie header value ("" if none).
/// `completion` runs on the main thread.
+ (void)cookiesForURL:(NSString *)url completion:(void (^)(NSString *cookieHeader))completion;

@end

NS_ASSUME_NONNULL_END

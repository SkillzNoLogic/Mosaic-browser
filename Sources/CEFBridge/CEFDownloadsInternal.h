// C++ hooks between CEFBridge.mm (the engine area's hub) and CEFDownloads.mm (downloads
// area). Not exported to Swift.

#pragma once

#include "include/cef_browser.h"
#include "include/cef_context_menu_handler.h"
#include "include/cef_download_handler.h"

/// The one download handler every Mosaic browser uses (CefClient::GetDownloadHandler).
CefRefPtr<CefDownloadHandler> MosaicDownloadHandler();

/// A context menu handler that only adds the downloads items, for a client that has no
/// context menu handler of its own (CefClient::GetContextMenuHandler).
CefRefPtr<CefContextMenuHandler> MosaicDownloadsContextMenuHandler();

/// Adds the downloads area's items to a page's context menu (from OnBeforeContextMenu).
void MosaicDownloadsBeforeContextMenu(CefRefPtr<CefBrowser> browser,
                                      CefRefPtr<CefContextMenuParams> params,
                                      CefRefPtr<CefMenuModel> model);

/// Runs one of those items (from OnContextMenuCommand). Returns false for other commands.
bool MosaicDownloadsContextMenuCommand(CefRefPtr<CefBrowser> browser,
                                       CefRefPtr<CefContextMenuParams> params,
                                       int command_id);

/// Any live browser, to start downloads no page asked for; null if there is none.
/// Defined in CEFBridge.mm, which owns the list of browsers.
CefRefPtr<CefBrowser> MosaicAnyLiveBrowser();

namespace Mosaic.Core.WindowsIntegration;

/// <summary>
/// Mosaic's icon names mapped to Windows icon-font glyphs. Pure data, so it is unit-tested on
/// any OS; <c>Mosaic.App.Features.System.Icons</c> turns entries into <c>FontIcon</c>s.
/// </summary>
/// <remarks>
/// <para><b>Names.</b> Two vocabularies, both accepted everywhere:</para>
/// <list type="bullet">
/// <item>Every SF Symbol name the macOS app uses (<c>Image(systemName:)</c>, <c>systemImage:</c>,
/// <c>symbol:</c> and the <c>symbol</c> properties in <c>Sources/</c>), so ported code reads the
/// same as the Swift it came from: <c>Icons.Make("puzzlepiece.extension")</c>.</item>
/// <item>Short semantic names for Windows-side code: <c>back</c>, <c>reload</c>, <c>newtab</c>,
/// <c>private</c>, <c>downloads</c>, <c>collections</c>, … (see <see cref="SemanticNames"/>).</item>
/// </list>
/// <para><b>Fonts.</b> Windows 11 ships <c>Segoe Fluent Icons</c>; Windows 10 ships
/// <c>Segoe MDL2 Assets</c>. Fluent keeps MDL2's code points, so most entries use one code point
/// for both; where the best Fluent glyph does not exist in MDL2, <see cref="Entry.Mdl2"/> names an
/// MDL2 substitute. Code points are from Microsoft's published tables
/// (learn.microsoft.com: "Segoe Fluent Icons font", "Segoe MDL2 Assets icons").</para>
/// <para><b>Choices worth knowing.</b> <c>chevron.left</c>/<c>chevron.right</c> (the macOS
/// toolbar's back/forward) map to Windows' Back/Forward arrows, as Edge draws them;
/// <c>xmark</c> is Cancel (also Stop loading); SF "fill" variants map to the solid glyph where
/// one exists, otherwise to the outline one. Ask the system area to add a name rather than
/// hard-coding a glyph; unknown names draw <see cref="Placeholder"/>.</para>
/// </remarks>
public static class IconGlyphs
{
    /// <summary>A glyph pair: the Segoe Fluent Icons code point and the Segoe MDL2 Assets one.</summary>
    public readonly record struct Entry(int Fluent, int Mdl2)
    {
        public Entry(int both) : this(both, both) { }

        public int For(bool fluent) => fluent ? Fluent : Mdl2;
    }

    /// <summary>"Unknown" (a question mark), drawn for names nobody has mapped.</summary>
    public const int Placeholder = 0xE9CE;

    /// <summary>
    /// Code points used in the table that exist only in Segoe Fluent Icons (not in
    /// Segoe MDL2 Assets); every entry using one carries a different MDL2 substitute.
    /// </summary>
    public static readonly IReadOnlySet<int> FluentOnly = new HashSet<int>
    {
        0xE733, // Blocked
        0xE736, // ReadingMode
        0xE7A1, // Contrast
        0xE917, // Clock
        0xECE4, // Blocked2
        0xED1A, // Hide
        0xF736, // WarningSolid
    };

    private static Entry E(int both) => new(both);
    private static Entry E(int fluent, int mdl2) => new(fluent, mdl2);

    // Segoe names in comments are Microsoft's glyph names.
    private static readonly Dictionary<string, Entry> table = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---- SF Symbols used by the macOS app (Sources/**) ----
        ["accessibility"] = E(0xE776),                     // EaseOfAccess
        ["archivebox"] = E(0xE7B8),                        // Package
        ["arrow.clockwise"] = E(0xE72C),                   // Refresh
        ["arrow.clockwise.circle"] = E(0xE72C),            // Refresh
        ["arrow.counterclockwise"] = E(0xE7A7),            // Undo (reset)
        ["arrow.down"] = E(0xE74B),                        // Down
        ["arrow.down.app"] = E(0xE896),                    // Download
        ["arrow.down.circle"] = E(0xE896),                 // Download
        ["arrow.down.circle.fill"] = E(0xE896),            // Download
        ["arrow.down.doc"] = E(0xE896),                    // Download (downloads page)
        ["arrow.left.and.right"] = E(0xE8AB),              // Switch (flexible space)
        ["arrow.left.square"] = E(0xE72B),                 // Back
        ["arrow.right.square"] = E(0xE72A),                // Forward
        ["arrow.triangle.2.circlepath"] = E(0xE895),       // Sync (switch engine)
        ["arrow.up.forward.app.fill"] = E(0xE7AC),         // OpenWith
        ["arrow.uturn.backward"] = E(0xE7A7),              // Undo
        ["arrow.uturn.backward.circle"] = E(0xE7A7),       // Undo
        ["arrow.uturn.backward.square"] = E(0xE7A7),       // Undo
        ["arrow.uturn.forward"] = E(0xE7A6),               // Redo
        ["bell"] = E(0xEA8F),                              // Ringer
        ["bell.fill"] = E(0xEA8F),                         // Ringer
        ["book"] = E(0xE82D),                              // Dictionary (open book)
        ["briefcase"] = E(0xE821),                         // Work
        ["camera"] = E(0xE722),                            // Camera
        ["cart"] = E(0xE7BF),                              // ShoppingCart
        ["checkmark"] = E(0xE73E),                         // CheckMark
        ["checkmark.circle"] = E(0xE930),                  // Completed
        ["checkmark.circle.fill"] = E(0xEC61),             // CompletedSolid
        ["checkmark.seal.fill"] = E(0xEB95),               // Certificate
        ["checkmark.shield"] = E(0xE83D),                  // DefenderApp
        ["chevron.down"] = E(0xE70D),                      // ChevronDown
        ["chevron.left"] = E(0xE72B),                      // Back (toolbar back button)
        ["chevron.right"] = E(0xE72A),                     // Forward (toolbar forward button)
        ["chevron.right.2"] = E(0xEB9D),                   // FastForward (bookmarks bar overflow)
        ["chevron.up"] = E(0xE70E),                        // ChevronUp
        ["circle"] = E(0xEA3A),                            // CircleRing
        ["circle.circle"] = E(0xECCB),                     // RadioBtnOn (Chromium engine)
        ["circle.dashed"] = E(0xF16A),                     // ProgressRingDots (soft gradient stop)
        ["circle.fill"] = E(0xEA3B),                       // CircleFill
        ["circle.lefthalf.filled"] = E(0xE7A1, 0xE793),    // Contrast / Light (appearance)
        ["clipboard"] = E(0xE77F),                         // Paste
        ["clock"] = E(0xE81C),                             // History
        ["command"] = E(0xEDA7),                           // KeyboardShortcut
        ["doc"] = E(0xE8A5),                               // Document
        ["doc.on.clipboard"] = E(0xE77F),                  // Paste
        ["doc.on.doc"] = E(0xE8C8),                        // Copy
        ["doc.plaintext"] = E(0xE736, 0xE82D),             // ReadingMode / Dictionary (reader)
        ["doc.plaintext.fill"] = E(0xE736, 0xE82D),        // ReadingMode / Dictionary
        ["doc.text"] = E(0xE8A5),                          // Document
        ["ellipsis"] = E(0xE712),                          // More
        ["ellipsis.circle"] = E(0xE712),                   // More
        ["engine.combustion"] = E(0xE9F5),                 // Processing (Engine settings)
        ["exclamationmark.arrow.circlepath"] = E(0xEA6A),  // SyncError
        ["exclamationmark.shield"] = E(0xE730),            // ReportHacked
        ["exclamationmark.triangle"] = E(0xE7BA),          // Warning
        ["exclamationmark.triangle.fill"] = E(0xF736, 0xE7BA), // WarningSolid / Warning
        ["eye"] = E(0xE890),                               // View
        ["eye.slash"] = E(0xED1A, 0xE9A9),                 // Hide / PasswordKeyHide
        ["eyedropper"] = E(0xEF3C),                        // Eyedropper
        ["eyeglasses"] = E(0xE736, 0xE82D),                // ReadingMode / Dictionary
        ["film"] = E(0xE8B2),                              // Movies
        ["flame"] = E(0xE945),                             // LightningBolt (Gecko engine)
        ["folder"] = E(0xE8B7),                            // Folder
        ["folder.badge.plus"] = E(0xE8F4),                 // NewFolder
        ["folder.fill"] = E(0xE8D5),                       // FolderFill
        ["gamecontroller"] = E(0xE7FC),                    // Game
        ["gauge.with.dots.needle.67percent"] = E(0xEC4A),  // SpeedHigh (Performance)
        ["gearshape"] = E(0xE713),                         // Settings
        ["globe"] = E(0xE774),                             // Globe
        ["graduationcap"] = E(0xE7BE),                     // Education
        ["hammer"] = E(0xE90F),                            // Repair
        ["hand.raised"] = E(0xE72E),                       // Lock (Privacy & Security)
        ["hand.raised.fill"] = E(0xE72E),                  // Lock
        ["hand.tap"] = E(0xE7C9),                          // TouchPointer
        ["heart"] = E(0xEB51),                             // Heart
        ["hourglass"] = E(0xE916),                         // Stopwatch
        ["house"] = E(0xE80F),                             // Home
        ["info.circle"] = E(0xE946),                       // Info
        ["info.circle.fill"] = E(0xF167),                  // InfoSolid
        ["key"] = E(0xE8D7),                               // Permissions (a key)
        ["key.fill"] = E(0xE8D7),                          // Permissions
        ["key.viewfinder"] = E(0xE928),                    // Fingerprint
        ["keyboard"] = E(0xE765),                          // KeyboardClassic
        ["lightbulb"] = E(0xEA80),                         // Lightbulb
        ["link"] = E(0xE71B),                              // Link
        ["location"] = E(0xE81D),                          // Location
        ["location.fill"] = E(0xE81D),                     // Location
        ["lock"] = E(0xE72E),                              // Lock
        ["lock.fill"] = E(0xE72E),                         // Lock
        ["lock.open"] = E(0xE785),                         // Unlock
        ["lock.open.trianglebadge.exclamationmark"] = E(0xE785), // Unlock (not secure)
        ["macwindow.badge.plus"] = E(0xE78B),              // NewWindow
        ["macwindow.on.rectangle"] = E(0xE8A7),            // OpenInNewWindow (pop-ups)
        ["magnifyingglass"] = E(0xE721),                   // Search
        ["magnifyingglass.circle"] = E(0xE721),            // Search
        ["menubar.rectangle"] = E(0xE8A4),                 // Bookmarks (bookmarks bar)
        ["mic"] = E(0xE720),                               // Microphone
        ["mic.fill"] = E(0xE720),                          // Microphone
        ["minus"] = E(0xE738),                             // Remove
        ["minus.circle"] = E(0xECC9),                      // RemoveFrom
        ["moon.zzz"] = E(0xE708),                          // QuietHours (sleeping tabs)
        ["music.note"] = E(0xEC4F),                        // MusicNote
        ["nosign"] = E(0xE733, 0xF140),                    // Blocked / StatusCircleBlock
        ["paintbrush"] = E(0xE771),                        // Personalize
        ["pause"] = E(0xE769),                             // Pause
        ["pause.circle"] = E(0xE769),                      // Pause
        ["person"] = E(0xE77B),                            // Contact
        ["person.crop.circle"] = E(0xE77B),                // Contact
        ["photo"] = E(0xE91B),                             // Photo
        ["pin"] = E(0xE718),                               // Pin
        ["pin.fill"] = E(0xE841),                          // PinFill
        ["pin.slash"] = E(0xE77A),                         // Unpin
        ["play.circle"] = E(0xE768),                       // Play
        ["play.rectangle"] = E(0xE714),                    // Video
        ["plus"] = E(0xE710),                              // Add
        ["plus.circle"] = E(0xECC8),                       // AddTo
        ["plus.square"] = E(0xE710),                       // Add
        ["plus.square.on.square"] = E(0xE8C8),             // Copy (duplicate)
        ["puzzlepiece"] = E(0xEA86),                       // Puzzle
        ["puzzlepiece.extension"] = E(0xEA86),             // Puzzle
        ["puzzlepiece.extension.fill"] = E(0xEA86),        // Puzzle
        ["rectangle.and.text.magnifyingglass"] = E(0xE721),// Search (find in page)
        ["rectangle.inset.filled.on.rectangle"] = E(0xE7F4), // TVMonitor (screen sharing)
        ["rectangle.stack.badge.plus"] = E(0xF168),        // GroupList (new tab group)
        ["rectangle.topthird.inset.filled"] = E(0xE737),   // Favicon (tab strip)
        ["shield"] = E(0xEA18),                            // Shield
        ["shield.lefthalf.filled"] = E(0xEA18),            // Shield (blocker on)
        ["shield.lefthalf.filled.slash"] = E(0xE733, 0xF140), // Blocked (blocker off)
        ["shield.slash"] = E(0xE733, 0xF140),              // Blocked
        ["shippingbox"] = E(0xE7B8),                       // Package
        ["sidebar.left"] = E(0xE8A0),                      // OpenPane
        ["sidebar.right"] = E(0xEA5B),                     // OpenPaneMirrored
        ["speaker.slash"] = E(0xE74F),                     // Mute
        ["speaker.slash.fill"] = E(0xE74F),                // Mute
        ["speaker.wave.2"] = E(0xE767),                    // Volume
        ["speaker.wave.2.fill"] = E(0xE767),               // Volume
        ["square.and.arrow.down"] = E(0xE8B5),             // Import (save / import)
        ["square.and.arrow.up"] = E(0xE72D),               // Share
        ["square.and.arrow.up.on.square"] = E(0xEDE1),     // Export
        ["square.grid.2x2"] = E(0xF0E2),                   // GridView (workspace default)
        ["square.grid.2x2.fill"] = E(0xF0E2),              // GridView
        ["square.grid.3x3.square"] = E(0xECA5),            // Tiles (speed dial)
        ["square.on.square"] = E(0xE8C8),                  // Copy
        ["square.stack"] = E(0xE7C4),                      // TaskView (workspaces)
        ["star"] = E(0xE734),                              // FavoriteStar
        ["star.fill"] = E(0xE735),                         // FavoriteStarFill
        ["star.slash"] = E(0xE8D9),                        // Unfavorite
        ["tag"] = E(0xE8EC),                               // Tag
        ["text.magnifyingglass"] = E(0xE721),              // Search
        ["textformat"] = E(0xE8D2),                        // Font
        ["textformat.size.larger"] = E(0xE8E8),            // FontIncrease
        ["textformat.size.smaller"] = E(0xE8E7),           // FontDecrease
        ["trash"] = E(0xE74D),                             // Delete
        ["video"] = E(0xE714),                             // Video
        ["video.fill"] = E(0xE714),                        // Video
        ["wrench.and.screwdriver"] = E(0xE90F),            // Repair
        ["xmark"] = E(0xE711),                             // Cancel
        ["xmark.circle"] = E(0xF13D),                      // StatusCircleErrorX
        ["xmark.circle.fill"] = E(0xEB90),                 // StatusErrorFull
        ["xmark.octagon"] = E(0xE783),                     // Error
        ["xmark.octagon.fill"] = E(0xEB90),                // StatusErrorFull
        ["xmark.square"] = E(0xE711),                      // Cancel

        // ---- Semantic names (Windows-side code) ----
        ["back"] = E(0xE72B),                              // Back
        ["forward"] = E(0xE72A),                           // Forward
        ["reload"] = E(0xE72C),                            // Refresh
        ["stop"] = E(0xE711),                              // Cancel
        ["home"] = E(0xE80F),                              // Home
        ["add"] = E(0xE710),                               // Add
        ["close"] = E(0xE711),                             // Cancel
        ["more"] = E(0xE712),                              // More
        ["menu"] = E(0xE700),                              // GlobalNavButton
        ["settings"] = E(0xE713),                          // Settings
        ["search"] = E(0xE721),                            // Search
        ["find"] = E(0xE721),                              // Search
        ["newtab"] = E(0xE710),                            // Add
        ["newwindow"] = E(0xE78B),                         // NewWindow
        ["private"] = E(0xE727),                           // InPrivate
        ["tabs"] = E(0xE737),                              // Favicon
        ["tabs.vertical"] = E(0xE8A0),                     // OpenPane
        ["workspace"] = E(0xE7C4),                         // TaskView
        ["collections"] = E(0xE8F1),                       // Library
        ["split"] = E(0xF740),                             // Split20 (split screen)
        ["sidebar"] = E(0xE8A0),                           // OpenPane
        ["download"] = E(0xE896),                          // Download
        ["downloads"] = E(0xE896),                         // Download
        ["upload"] = E(0xE898),                            // Upload
        ["favorite"] = E(0xE734),                          // FavoriteStar
        ["favorite.fill"] = E(0xE735),                     // FavoriteStarFill
        ["bookmarks"] = E(0xE728),                         // FavoriteList
        ["history"] = E(0xE81C),                           // History
        ["readinglist"] = E(0xE7BC),                       // ReadingList
        ["reader"] = E(0xE736, 0xE82D),                    // ReadingMode / Dictionary
        ["extensions"] = E(0xEA86),                        // Puzzle
        ["passwords"] = E(0xE8D7),                         // Permissions (key)
        ["blocker"] = E(0xEA18),                           // Shield
        ["blocker.off"] = E(0xE733, 0xF140),               // Blocked / StatusCircleBlock
        ["permissions"] = E(0xE8D7),                       // Permissions
        ["notifications"] = E(0xEA8F),                     // Ringer
        ["popups"] = E(0xE8A7),                            // OpenInNewWindow
        ["share"] = E(0xE72D),                             // Share
        ["print"] = E(0xE749),                             // Print
        ["save"] = E(0xE74E),                              // Save
        ["saveas"] = E(0xE792),                            // SaveAs
        ["open"] = E(0xE8E5),                              // OpenFile
        ["openfolder"] = E(0xE838),                        // FolderOpen
        ["import"] = E(0xE8B5),                            // Import
        ["export"] = E(0xEDE1),                            // Export
        ["copy"] = E(0xE8C8),                              // Copy
        ["cut"] = E(0xE8C6),                               // Cut
        ["paste"] = E(0xE77F),                             // Paste
        ["selectall"] = E(0xE8B3),                         // SelectAll
        ["delete"] = E(0xE74D),                            // Delete
        ["edit"] = E(0xE70F),                              // Edit
        ["rename"] = E(0xE8AC),                            // Rename
        ["undo"] = E(0xE7A7),                              // Undo
        ["redo"] = E(0xE7A6),                              // Redo
        ["sync"] = E(0xE895),                              // Sync
        ["pin.tab"] = E(0xE718),                           // Pin
        ["unpin"] = E(0xE77A),                             // Unpin
        ["mute"] = E(0xE74F),                              // Mute
        ["volume"] = E(0xE767),                            // Volume
        ["zoom.in"] = E(0xE8A3),                           // ZoomIn
        ["zoom.out"] = E(0xE71F),                          // ZoomOut
        ["fullscreen"] = E(0xE740),                        // FullScreen
        ["fullscreen.exit"] = E(0xE73F),                   // BackToWindow
        ["pip"] = E(0xE944),                               // ReturnToWindow (picture in picture)
        ["devtools"] = E(0xEC7A),                          // DeveloperTools
        ["code"] = E(0xE943),                              // Code
        ["screenshot"] = E(0xF7ED),                        // WindowSnipping
        ["camera.page"] = E(0xE722),                       // Camera
        ["microphone"] = E(0xE720),                        // Microphone
        ["info"] = E(0xE946),                              // Info
        ["warning"] = E(0xE7BA),                           // Warning
        ["error"] = E(0xE783),                             // Error
        ["success"] = E(0xE930),                           // Completed
        ["help"] = E(0xE897),                              // Help
        ["shortcuts"] = E(0xEDA7),                         // KeyboardShortcut
        ["palette"] = E(0xE790),                           // Color
        ["appearance"] = E(0xE790),                        // Color
        ["theme"] = E(0xE7A1, 0xE793),                     // Contrast / Light
        ["layout"] = E(0xF246),                            // ViewDashboard
        ["canvas"] = E(0xF246),                            // ViewDashboard
        ["gradient"] = E(0xE771),                          // Personalize
        ["engine"] = E(0xE9F5),                            // Processing
        ["gecko"] = E(0xE945),                             // LightningBolt
        ["chromium"] = E(0xECCB),                          // RadioBtnOn
        ["performance"] = E(0xEC4A),                       // SpeedHigh
        ["sleep"] = E(0xE708),                             // QuietHours
        ["updates"] = E(0xE777),                           // UpdateRestore
        ["about"] = E(0xE946),                             // Info
        ["language"] = E(0xE775),                          // TimeLanguage
        ["website"] = E(0xEB41),                           // Website
        ["web"] = E(0xE774),                               // Globe
        ["defaultbrowser"] = E(0xE774),                    // Globe
        ["feedback"] = E(0xED15),                          // Feedback
        ["clear"] = E(0xE894),                             // Clear
        ["filter"] = E(0xE71C),                            // Filter
        ["sort"] = E(0xE8CB),                              // Sort
        ["list"] = E(0xEA37),                              // List
        ["grid"] = E(0xF0E2),                              // GridView
        ["speeddial"] = E(0xECA5),                         // Tiles
        ["command.palette"] = E(0xE756),                   // CommandPrompt
        ["user"] = E(0xE77B),                              // Contact
        ["certificate"] = E(0xEB95),                       // Certificate
        ["minimize"] = E(0xE921),                          // ChromeMinimize
        ["maximize"] = E(0xE922),                          // ChromeMaximize
        ["restore"] = E(0xE923),                           // ChromeRestore
        ["chrome.close"] = E(0xE8BB),                      // ChromeClose
        ["chevron.down.small"] = E(0xE96E),                // ChevronDownSmall
        ["chevron.up.small"] = E(0xE96D),                  // ChevronUpSmall
        ["chevron.left.small"] = E(0xE96F),                // ChevronLeftSmall
        ["chevron.right.small"] = E(0xE970),               // ChevronRightSmall
        ["chevron.left.plain"] = E(0xE76B),                // ChevronLeft
        ["chevron.right.plain"] = E(0xE76C),               // ChevronRight
    };

    /// <summary>The semantic (non-SF-Symbol) names, for documentation and tests.</summary>
    public static readonly IReadOnlyList<string> SemanticNames =
    [
        "back", "forward", "reload", "stop", "home", "add", "close", "more", "menu", "settings",
        "search", "find", "newtab", "newwindow", "private", "tabs", "tabs.vertical", "workspace",
        "collections", "split", "sidebar", "download", "downloads", "upload", "favorite",
        "favorite.fill", "bookmarks", "history", "readinglist", "reader", "extensions",
        "passwords", "blocker", "blocker.off", "permissions", "notifications", "popups", "share",
        "print", "save", "saveas", "open", "openfolder", "import", "export", "copy", "cut",
        "paste", "selectall", "delete", "edit", "rename", "undo", "redo", "sync", "pin.tab",
        "unpin", "mute", "volume", "zoom.in", "zoom.out", "fullscreen", "fullscreen.exit", "pip",
        "devtools", "code", "screenshot", "camera.page", "microphone", "info", "warning", "error",
        "success", "help", "shortcuts", "palette", "appearance", "theme", "layout", "canvas",
        "gradient", "engine", "gecko", "chromium", "performance", "sleep", "updates", "about",
        "language", "website", "web", "defaultbrowser", "feedback", "clear", "filter", "sort",
        "list", "grid", "speeddial", "command.palette", "user", "certificate", "minimize",
        "maximize", "restore", "chrome.close", "chevron.down.small", "chevron.up.small",
        "chevron.left.small", "chevron.right.small", "chevron.left.plain", "chevron.right.plain",
    ];

    /// <summary>Every mapped name (SF Symbols and semantic names).</summary>
    public static IEnumerable<string> Names => table.Keys;

    public static bool Contains(string name) => table.ContainsKey(name);

    public static bool TryGet(string name, out Entry entry) => table.TryGetValue(name, out entry);

    /// <summary>The code point for <paramref name="name"/> in the chosen font, or <see cref="Placeholder"/>.</summary>
    public static int CodePoint(string name, bool fluent) =>
        table.TryGetValue(name, out var entry) ? entry.For(fluent) : Placeholder;

    /// <summary>The glyph text for <paramref name="name"/> in the chosen font.</summary>
    public static string Glyph(string name, bool fluent) => char.ConvertFromUtf32(CodePoint(name, fluent));

    /// <summary>Adds or replaces a mapping (the app's <c>Icons.Add</c>).</summary>
    public static void Set(string name, Entry entry)
    {
        lock (table) table[name] = entry;
    }

    /// <summary>Font family names and the files Windows installs them as.</summary>
    public const string FluentFontName = "Segoe Fluent Icons";
    public const string Mdl2FontName = "Segoe MDL2 Assets";
    public const string FluentFontFile = "SegoeIcons.ttf";
    public const string Mdl2FontFile = "segmdl2.ttf";

    /// <summary>
    /// Decides which font to use from what is installed. <paramref name="overrideValue"/> is the
    /// <c>MOSAIC_ICON_FONT</c> environment variable (<c>fluent</c> or <c>mdl2</c>), which lets CI
    /// exercise the Windows 10 path on a Windows 11 runner.
    /// </summary>
    public static bool ChooseFluent(bool fluentInstalled, string? overrideValue) =>
        overrideValue?.Trim().ToLowerInvariant() switch
        {
            "mdl2" or "windows10" or "segoe mdl2 assets" => false,
            "fluent" or "windows11" or "segoe fluent icons" => true,
            _ => fluentInstalled,
        };
}

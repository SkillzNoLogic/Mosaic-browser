import Foundation

/// Query parameters that only exist to track people across sites, and their removal
/// from addresses before they are loaded.
///
/// Sources:
/// - Firefox's query-stripping list, the Remote Settings collection
///   `main/query-stripping` that Firefox's own "strip tracking parameters" feature
///   uses (snapshot taken 2026-11-27):
///   https://firefox.settings.services.mozilla.com/v1/buckets/main/collections/query-stripping/records
/// - Google Analytics' campaign parameters (`utm_*`), which Firefox does not strip
///   by default but Brave and AdGuard's URL Tracking filter do. They are matched by
///   prefix here and listed by name for Gecko, whose pref takes exact names only.
///
/// Firefox's allow-list (`googleadservices.com`, whose redirects need `gclid`) is
/// honoured the same way.
public enum TrackingParameters {
    /// Firefox's list, exactly as published.
    public static let firefoxList: [String] = [
        "gclid", "dclid", "msclkid", "_openstat", "yclid", "wickedid", "twclid", "_hsenc", "__hssc", "__hstc",
        "__hsfp", "hsctatracking", "wbraid", "gbraid", "ysclid", "mc_eid", "oly_anon_id", "oly_enc_id", "__s",
        "vero_id", "mkt_tok", "fbclid",
    ]

    /// Campaign parameters, by name (for engines that cannot match a prefix).
    public static let campaignParameters: [String] = [
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id", "utm_name",
        "utm_source_platform", "utm_creative_format", "utm_marketing_tactic", "utm_cid", "utm_reader",
        "utm_referrer", "utm_social", "utm_social-type", "utm_brand", "utm_place", "utm_pubreferrer", "utm_swu",
        "utm_viz_id",
    ]

    /// Every exact name, lower-cased; what Gecko's `privacy.query_stripping.strip_list` gets.
    public static var allNames: [String] { firefoxList + campaignParameters }

    /// Sites whose own links need the parameters (Firefox's allow-list).
    public static let allowedSites: Set<String> = ["googleadservices.com"]

    private static let names: Set<String> = Set(allNames)

    /// Whether `name` is a tracking parameter (case-insensitive; any `utm_` name counts).
    public static func isTracking(_ name: String) -> Bool {
        let lowered = name.lowercased()
        return names.contains(lowered) || lowered.hasPrefix("utm_")
    }

    /// `url` without its tracking parameters, or nil when it has none (or is not a
    /// web address, or belongs to an allowed site). Everything else about the address
    /// is kept byte for byte, including the order and spelling of other parameters
    /// and the fragment.
    public static func stripped(_ url: String) -> String? {
        guard let queryStart = url.firstIndex(of: "?") else { return nil }
        let lowered = url.lowercased()
        guard lowered.hasPrefix("http://") || lowered.hasPrefix("https://") else { return nil }
        if let host = URLComponents(string: url)?.host?.lowercased(),
           allowedSites.contains(where: { host == $0 || host.hasSuffix("." + $0) }) {
            return nil
        }
        let fragmentStart = url[queryStart...].firstIndex(of: "#") ?? url.endIndex
        let query = url[url.index(after: queryStart)..<fragmentStart]
        let pairs = query.split(separator: "&", omittingEmptySubsequences: false)
        let kept = pairs.filter { pair in
            let name = pair.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false).first ?? ""
            let decoded = String(name).removingPercentEncoding ?? String(name)
            return !isTracking(decoded)
        }
        guard kept.count != pairs.count else { return nil }
        let base = String(url[..<queryStart])
        let fragment = String(url[fragmentStart...])
        return kept.isEmpty ? base + fragment : base + "?" + kept.joined(separator: "&") + fragment
    }
}

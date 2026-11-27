import Foundation

/// A named set of tabs ("Work", "School"…). Every tab belongs to exactly one workspace.
/// Shapes shared by the tabs area (which owns the behaviour) and the shell (which draws them).
struct Workspace: Identifiable, Hashable, Codable {
    var id = UUID()
    var name: String
    /// SF Symbol shown in the workspace switcher.
    var symbol = "square.grid.2x2"

    static let personal = Workspace(name: "Personal", symbol: "person")
}

/// A collapsible, named, coloured group of tabs within a workspace.
struct TabGroup: Identifiable, Hashable, Codable {
    enum Color: String, Codable, CaseIterable { case grey, blue, red, yellow, green, pink, purple, cyan, orange }

    var id = UUID()
    var name: String
    var color: Color = .blue
    var isCollapsed = false
}

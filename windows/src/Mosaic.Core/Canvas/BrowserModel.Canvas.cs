using Mosaic.Core.Canvas;

namespace Mosaic.Core.Browser;

// canvas: the layout canvas, Gradient Studio and configurations (macOS: `lazy var canvas`).
public sealed partial class BrowserModel
{
    private CanvasController? canvas;

    /// <summary>Created on first use (opening the editor, an automation command), never at start-up.</summary>
    public CanvasController Canvas => canvas ??= CanvasServices.Create(this);

    /// <summary>Whether the canvas has been created (so <c>dump</c> can report without creating it).</summary>
    public bool HasCanvas => canvas is not null;

    /// <summary>Tests: a canvas with its own library file instead of the user's.</summary>
    internal void UseCanvas(CanvasController controller) => canvas = controller;
}

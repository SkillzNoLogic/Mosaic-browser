using Mosaic.Core.Browser;
using Mosaic.Core.Permissions;

namespace Mosaic.Core.Engine;

/// <summary>A permission request waiting for the person, shown over the page while its tab is selected.</summary>
public sealed class PermissionPrompt
{
    internal PermissionPrompt(Guid tabId, EngineKind engine, PermissionRequestInfo request, string? pageSite)
    {
        TabId = tabId;
        Engine = engine;
        Request = request;
        PageSite = pageSite;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public Guid TabId { get; }
    public EngineKind Engine { get; }

    /// <summary>What is asked; grows when the engine asks for camera and microphone separately.</summary>
    public PermissionRequestInfo Request { get; internal set; }

    /// <summary>The site of the page shown when it asked; leaving that site drops the prompt.</summary>
    public string? PageSite { get; }

    /// <summary>Honest limits that apply to this request in this engine on this PC.</summary>
    public IReadOnlyList<string> Notes { get; internal set; } = [];

    /// <summary>Devices Windows' privacy settings would refuse; the prompt offers their Settings pages.</summary>
    public IReadOnlyList<WindowsCapability> BlockedByWindows { get; internal set; } = [];

    /// <summary>The engine requests this prompt answers: Gecko request ids (int) or <see cref="IPermissionResponder"/>s.</summary>
    internal List<object> Tokens { get; } = [];

    public string Sentence => Request.Sentence;
}

/// <summary>Pop-ups a page tried to open without the person asking for them.</summary>
public sealed class PopupNotice
{
    internal PopupNotice(Guid tabId, EngineKind engine, string? site)
    {
        TabId = tabId;
        Engine = engine;
        Site = site;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public Guid TabId { get; }
    public EngineKind Engine { get; }

    /// <summary>The site of the page that tried; "Always allow" remembers it. Null when not a web page.</summary>
    public string? Site { get; }

    public List<string> Urls { get; } = [];
    public int Count { get; internal set; }

    public string Title => Count == 1 ? "Pop-up blocked" : $"{Count} pop-ups blocked";
}

/// <summary>A JavaScript dialog (alert, confirm, prompt, leave page) of a Chromium page. Gecko draws its own.</summary>
public sealed class PageDialog
{
    internal PageDialog(Guid tabId, PageDialogKind kind, string message, string defaultText, string origin, IDialogResponder responder)
    {
        TabId = tabId;
        Kind = kind;
        Message = message;
        DefaultText = defaultText;
        Origin = origin;
        Responder = responder;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public Guid TabId { get; }
    public PageDialogKind Kind { get; }
    public string Message { get; }
    public string DefaultText { get; }

    /// <summary>Who is asking, as shown: "example.com" (empty when the page has no web origin).</summary>
    public string Origin { get; }

    internal IDialogResponder Responder { get; }

    public string Heading => Origin.Length == 0 ? "This page says" : $"{Origin} says";
    public string AcceptLabel => Kind == PageDialogKind.BeforeUnload ? "Leave" : "OK";
    public string CancelLabel => Kind == PageDialogKind.BeforeUnload ? "Stay" : "Cancel";
    public bool HasCancel => Kind != PageDialogKind.Alert;
    public bool HasTextField => Kind == PageDialogKind.Prompt;

    public static string KindName(PageDialogKind kind) => kind switch
    {
        PageDialogKind.Alert => "alert",
        PageDialogKind.Confirm => "confirm",
        PageDialogKind.Prompt => "prompt",
        _ => "beforeunload",
    };
}

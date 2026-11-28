namespace Mosaic.Core.Downloads;

/// <summary>A question about a download, waiting for an answer.</summary>
/// <param name="Kind">"fdm", "duplicate", "safety" or "save".</param>
/// <param name="Title">The question.</param>
/// <param name="Message">Explanation under it (for "save": the folder offered).</param>
/// <param name="Buttons">Button titles; the last one is always the cautious choice.</param>
/// <param name="IsWarning">A safety warning: Enter picks the last (cautious) button.</param>
/// <param name="SuggestedName">For "save": the file name offered.</param>
public sealed record DownloadQuestion(
    string Kind, string Title, string Message, IReadOnlyList<string> Buttons, bool IsWarning = false,
    string? SuggestedName = null)
{
    internal TaskCompletionSource<int?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal string? ChosenPath { get; set; }
}

/// <summary>
/// Questions about downloads (duplicate file, Free Download Manager, safety, where to save), one
/// at a time. The app shows <see cref="Pending"/> (a dialog inside Mosaic's window, or the
/// system's Save dialog) and calls <see cref="Answer"/> / <see cref="Save"/>; scripted runs answer
/// with the <c>downloadPromptAnswer</c> / <c>downloadPromptSave</c> automation commands.
/// </summary>
/// <remarks>Port of <c>DownloadPrompter.swift</c>, without the UI, so it can be tested.</remarks>
public sealed class DownloadPrompter
{
    private readonly Queue<DownloadQuestion> waiting = new();

    /// <summary>The question being shown, if any.</summary>
    public DownloadQuestion? Pending { get; private set; }

    /// <summary>Raised (on the UI thread) whenever <see cref="Pending"/> changes.</summary>
    public event Action? PendingChanged;

    /// <summary>Asks a question; returns the index of the button pressed (0 = first), or null when dismissed.</summary>
    public Task<int?> AskAsync(string kind, string title, string message, IReadOnlyList<string> buttons, bool isWarning = false) =>
        Enqueue(new DownloadQuestion(kind, title, message, buttons, isWarning));

    /// <summary>"Save As": returns the chosen full path, or null when cancelled.</summary>
    public async Task<string?> ChooseDestinationAsync(string suggestedName, string directory)
    {
        var question = new DownloadQuestion("save", suggestedName, directory, ["Save", "Cancel"], SuggestedName: suggestedName);
        var answer = await Enqueue(question);
        if (answer != 0) return null;
        return question.ChosenPath ?? Path.Combine(directory, suggestedName);
    }

    /// <summary>Presses button <paramref name="index"/> of the question on screen (for "save": 0 = Save, 1 = Cancel).</summary>
    public bool Answer(int? index)
    {
        if (Pending is not { } question) return false;
        Finish(question, index is { } i && i >= 0 && i < question.Buttons.Count ? i : question.Buttons.Count - 1);
        return true;
    }

    /// <summary>Saves the open "Save As" question as <paramref name="path"/>.</summary>
    public bool Save(string path)
    {
        if (Pending is not { Kind: "save" } question) return false;
        question.ChosenPath = path;
        Finish(question, 0);
        return true;
    }

    /// <summary>Dismisses every question (shutdown); each counts as the cautious choice.</summary>
    public void CancelAll()
    {
        while (waiting.TryDequeue(out var question)) question.Answer.TrySetResult(null);
        if (Pending is { } pending) Finish(pending, null);
    }

    private Task<int?> Enqueue(DownloadQuestion question)
    {
        waiting.Enqueue(question);
        if (Pending is null) ShowNext();
        return question.Answer.Task;
    }

    private void Finish(DownloadQuestion question, int? index)
    {
        if (!ReferenceEquals(question, Pending)) return;
        Pending = null;
        question.Answer.TrySetResult(index);
        ShowNext();
    }

    private void ShowNext()
    {
        Pending = waiting.TryDequeue(out var next) ? next : null;
        PendingChanged?.Invoke();
    }
}

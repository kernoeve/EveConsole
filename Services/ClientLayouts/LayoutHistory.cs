namespace EveConsole.Services.ClientLayouts;

/// <summary>
/// Undo and redo for one layout in the editor: the layout as it was after each change, kept whole.
///
/// <para>Whole copies rather than a record of each kind of change, so nothing the editor can do is
/// left out: the canvas's menus and drags, the boxes beside it, the dock order. A step is whatever
/// changed between two saves, so a name typed in one go, or a colour dragged across the picker, is
/// one step, not one per key or per colour.</para>
/// </summary>
public sealed class LayoutHistory
{
    /// <summary>How many steps back are kept. A layout is a few kilobytes as text.</summary>
    public const int Limit = 200;

    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];

    /// <summary>The layout as it was last recorded: where an undo goes back from.</summary>
    private string _known;

    /// <param name="root">The layout before anything is changed.</param>
    public LayoutHistory(LayoutArea root) => _known = ClientLayoutJson.Write(root);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Takes the layout as it is now as a step, if it changed since the last. A new change ends
    /// any redo, as it does everywhere else.
    /// </summary>
    public bool Record(LayoutArea root)
    {
        var now = ClientLayoutJson.Write(root);
        if (now == _known) return false;
        _undo.Add(_known);
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
        _known = now;
        return true;
    }

    /// <summary>Puts the layout back one step. Anything not yet recorded is recorded first, so it can be redone.</summary>
    public bool Undo(LayoutArea root)
    {
        Record(root);
        if (_undo.Count == 0) return false;
        _redo.Add(_known);
        _known = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(root, _known);
        return true;
    }

    public bool Redo(LayoutArea root)
    {
        // Something changed since the undo: that is a new step, and there is nothing to redo.
        if (Record(root) || _redo.Count == 0) return false;
        _undo.Add(_known);
        _known = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Restore(root, _known);
        return true;
    }

    /// <summary>
    /// ⚠️ Into the same object, not a new one in its place. The layout's top area is held by the
    /// rows, monitors and canvas that show it, and they would go on showing the one replaced.
    /// Every property is copied, so one added to <see cref="LayoutArea"/> later comes back too.
    /// </summary>
    private static void Restore(LayoutArea into, string saved)
    {
        var from = ClientLayoutJson.Read(saved);
        foreach (var property in typeof(LayoutArea).GetProperties())
            if (property.CanRead && property.CanWrite) property.SetValue(into, property.GetValue(from));
    }
}

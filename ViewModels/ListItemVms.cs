using Avalonia.Media;
using EveConsole.Models;

namespace EveConsole.ViewModels;

public class CharacterListItem(Character character, string statusLabel, IBrush statusBrush, string? statusTip = null)
{
    public Character Character   { get; } = character;
    public string    StatusLabel { get; } = statusLabel;
    public IBrush    StatusBrush { get; } = statusBrush;
    /// <summary>The longer story behind the label, or null for no tooltip — an empty string would show an empty one.</summary>
    public string?   StatusTip   { get; } = statusTip;
}

public class CorpListItem(Corporation corp, string statusLabel, IBrush statusBrush, string? statusTip = null)
{
    public Corporation Corp        { get; } = corp;
    public string      StatusLabel { get; } = statusLabel;
    public IBrush      StatusBrush { get; } = statusBrush;
    public string?     StatusTip   { get; } = statusTip;
}

using Avalonia.Media;
using EveConsole.Models;

namespace EveConsole.ViewModels;

public class CharacterListItem(Character character, string statusLabel, IBrush statusBrush)
{
    public Character Character  { get; } = character;
    public string    StatusLabel { get; } = statusLabel;
    public IBrush    StatusBrush { get; } = statusBrush;
}

public class CorpListItem(Corporation corp, string statusLabel, IBrush statusBrush)
{
    public Corporation Corp        { get; } = corp;
    public string      StatusLabel { get; } = statusLabel;
    public IBrush      StatusBrush { get; } = statusBrush;
}

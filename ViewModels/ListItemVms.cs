using Avalonia.Media;
using EveCortex.Models;

namespace EveCortex.ViewModels;

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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class CharacterViewerView : ReactiveUserControl<CharacterViewerViewModel>
{
    public CharacterViewerView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens the double-clicked character on the Detail tab.
    ///
    /// <para>Handled on the grid rather than per row so it keeps working as rows are recycled,
    /// and it walks up from whatever was actually hit to find the row — a double-click lands on
    /// the TextBlock inside a cell, not on the row itself. A double-click on the header or on
    /// empty space below the rows finds nothing and is ignored.</para>
    /// </summary>
    private void OnSummaryRowActivated(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual hit) return;
        if (hit.FindAncestorOfType<DataGridRow>() is not { DataContext: CharacterSummaryRowVm row })
            return;

        ViewModel?.ShowDetailFor(row.CharacterId);
    }
}

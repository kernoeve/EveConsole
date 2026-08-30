using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Models;
using EveConsole.Services;

namespace EveConsole.Views;

public partial class StandingProjectDialog : Window
{
    private readonly CorpActivityService _service;
    private CancellationTokenSource? _cts;

    // Selections
    private int?   _selectedTypeId;
    private string _selectedTypeName    = "";
    private long?  _selectedStationId;
    private string _selectedStationName = "";
    private int?   _selectedSystemId;
    private string _selectedSystemName  = "";
    private int?   _selectedRegionId;
    private string _selectedRegionName  = "";
    private int?   _selectedConstId;
    private string _selectedConstName   = "";

    /// <summary>One alliance in the picker. Named rather than shown as an id, obviously.</summary>
    private sealed record AllianceRow(long Id, string Name)
    {
        public override string ToString() => Name;
    }

    public StandingProjectDialog(CorpActivityService service, CorpStandingProject? existing = null)
    {
        InitializeComponent();
        _service = service;

        // ⚠️ Loaded before Populate, so an existing project can select its own alliance out of a
        // list that is already there. Fired and awaited inside, because a constructor cannot await
        // — Populate re-selects once the rows land.
        _ = LoadAlliancesAsync(existing?.ScopeEntityId);

        if (existing is not null)
            Populate(existing);
        else
            TypeCombo.SelectedIndex = 1; // default to destroy_npc
    }

    private async Task LoadAlliancesAsync(int? selectId)
    {
        List<(long Id, string Name)> rows;
        try   { rows = await _service.GetTrackedAlliancesAsync(); }
        catch { rows = []; }

        AllianceCombo.ItemsSource = rows.Select(a => new AllianceRow(a.Id, a.Name)).ToList();

        // Said plainly rather than left as an empty dropdown, which reads as "still loading".
        AllianceEmptyLabel.IsVisible = rows.Count == 0;
        AllianceCombo.IsVisible      = rows.Count > 0;

        if (AllianceCombo.ItemsSource is IEnumerable<AllianceRow> list)
            AllianceCombo.SelectedItem = selectId is > 0
                ? list.FirstOrDefault(a => a.Id == selectId.Value)
                : list.FirstOrDefault();
    }

    private void Populate(CorpStandingProject p)
    {
        if (p.ProjectType == "deliver_item")
        {
            TypeCombo.SelectedIndex = 0;
            if (p.ItemTypeId.HasValue)
            {
                _selectedTypeId             = p.ItemTypeId;
                _selectedTypeName           = p.ItemTypeName;
                ItemSelectedLabel.Text      = p.ItemTypeName;
                ItemSelectedLabel.IsVisible = true;
            }
            if (p.StationId.HasValue)
            {
                _selectedStationId          = p.StationId;
                _selectedStationName        = p.StationName;
                StationSelectedLabel.Text   = p.StationName;
                StationSelectedLabel.IsVisible = true;
            }
            return;
        }

        TypeCombo.SelectedIndex = 1;
        switch (p.ScopeType)
        {
            case "region_adm":
                ScopeRegionAdm.IsChecked = true;
                if (p.ScopeEntityId.HasValue)
                {
                    _selectedRegionId             = p.ScopeEntityId;
                    _selectedRegionName           = p.ScopeEntityName;
                    RegionSelectedLabel.Text      = p.ScopeEntityName;
                    RegionSelectedLabel.IsVisible = true;
                }
                RegionAdmBox.Value = (decimal)(p.MinAdm ?? 4.0);
                break;
            case "constellation_adm":
                ScopeConstAdm.IsChecked = true;
                if (p.ScopeEntityId.HasValue)
                {
                    _selectedConstId             = p.ScopeEntityId;
                    _selectedConstName           = p.ScopeEntityName;
                    ConstSelectedLabel.Text      = p.ScopeEntityName;
                    ConstSelectedLabel.IsVisible = true;
                }
                ConstAdmBox.Value = (decimal)(p.MinAdm ?? 4.0);
                break;
            case "alliance_sov":
                ScopeAllianceSov.IsChecked = true;
                // The combo is filled asynchronously; LoadAlliancesAsync re-selects this id when
                // the rows arrive.
                break;
            default:
                ScopeSystem.IsChecked = true;
                if (p.SolarSystemId.HasValue)
                {
                    _selectedSystemId             = p.SolarSystemId;
                    _selectedSystemName           = p.SolarSystemName;
                    SystemSelectedLabel.Text      = p.SolarSystemName;
                    SystemSelectedLabel.IsVisible = true;
                }
                break;
        }
        UpdateScopePanels();
    }

    private void OnTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var isDeliver = TypeCombo.SelectedIndex == 0;
        DeliverPanel.IsVisible = isDeliver;
        DestroyPanel.IsVisible = !isDeliver;
    }

    private void OnScopeChanged(object? sender, RoutedEventArgs e)
    {
        SystemPanel.IsVisible = ReferenceEquals(sender, ScopeSystem);
        RegionPanel.IsVisible = ReferenceEquals(sender, ScopeRegionAdm);
        ConstPanel.IsVisible  = ReferenceEquals(sender, ScopeConstAdm);
    }

    private void UpdateScopePanels()
    {
        SystemPanel.IsVisible   = ScopeSystem.IsChecked == true;
        RegionPanel.IsVisible   = ScopeRegionAdm.IsChecked == true;
        ConstPanel.IsVisible    = ScopeConstAdm.IsChecked == true;
        AlliancePanel.IsVisible = ScopeAllianceSov.IsChecked == true;
    }

    // ── Item type search ──────────────────────────────────────────────────────

    private async void OnItemSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _selectedTypeId = null;
        _selectedTypeName = "";
        ItemSelectedLabel.IsVisible = false;
        ItemResultsBorder.IsVisible = false;

        var text = ItemSearchBox.Text ?? "";
        if (text.Length < 2) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            await Task.Delay(250, ct);
            var results = await _service.SearchSdeTypesAsync(text, ct);
            if (ct.IsCancellationRequested) return;
            ItemResultsList.ItemsSource = results;
            ItemResultsBorder.IsVisible = results.Count > 0;
        }
        catch (Exception) { }
    }

    private void OnItemSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ItemResultsList.SelectedItem is not SdeTypeResult r) return;
        _selectedTypeId   = r.TypeId;
        _selectedTypeName = r.Name;
        ItemResultsBorder.IsVisible = false;
        ItemSelectedLabel.Text      = r.Name;
        ItemSelectedLabel.IsVisible = true;
    }

    // ── Station / structure search ────────────────────────────────────────────

    private async void OnStationSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _selectedStationId = null;
        _selectedStationName = "";
        StationSelectedLabel.IsVisible = false;
        StationResultsBorder.IsVisible = false;

        var text = StationSearchBox.Text ?? "";
        if (text.Length < 2) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            await Task.Delay(250, ct);
            var results = await _service.SearchSdeStationsAsync(text, ct);
            if (ct.IsCancellationRequested) return;
            StationResultsList.ItemsSource = results;
            StationResultsBorder.IsVisible = results.Count > 0;
        }
        catch (Exception) { }
    }

    private void OnStationSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (StationResultsList.SelectedItem is not SdeStationResult r) return;
        _selectedStationId   = r.StationId;
        _selectedStationName = r.Name;
        StationResultsBorder.IsVisible = false;
        StationSelectedLabel.Text      = r.Name;
        StationSelectedLabel.IsVisible = true;
    }

    // ── System search ─────────────────────────────────────────────────────────

    private async void OnSystemSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _selectedSystemId = null;
        _selectedSystemName = "";
        SystemSelectedLabel.IsVisible = false;
        SystemResultsBorder.IsVisible = false;

        var text = SystemSearchBox.Text ?? "";
        if (text.Length < 2) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            await Task.Delay(250, ct);
            var results = await _service.SearchSdeSystemsAsync(text, ct);
            if (ct.IsCancellationRequested) return;
            SystemResultsList.ItemsSource = results;
            SystemResultsBorder.IsVisible = results.Count > 0;
        }
        catch (Exception) { }
    }

    private void OnSystemSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (SystemResultsList.SelectedItem is not SdeSystemResult r) return;
        _selectedSystemId   = r.SystemId;
        _selectedSystemName = r.Name;
        SystemResultsBorder.IsVisible = false;
        SystemSelectedLabel.Text      = r.Name;
        SystemSelectedLabel.IsVisible = true;
    }

    // ── Region search ─────────────────────────────────────────────────────────

    private async void OnRegionSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _selectedRegionId = null;
        _selectedRegionName = "";
        RegionSelectedLabel.IsVisible = false;
        RegionResultsBorder.IsVisible = false;

        var text = RegionSearchBox.Text ?? "";
        if (text.Length < 2) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            await Task.Delay(250, ct);
            var results = await _service.SearchSdeRegionsAsync(text, ct);
            if (ct.IsCancellationRequested) return;
            RegionResultsList.ItemsSource = results;
            RegionResultsBorder.IsVisible = results.Count > 0;
        }
        catch (Exception) { }
    }

    private void OnRegionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (RegionResultsList.SelectedItem is not SdeRegionResult r) return;
        _selectedRegionId   = r.RegionId;
        _selectedRegionName = r.Name;
        RegionResultsBorder.IsVisible = false;
        RegionSelectedLabel.Text      = r.Name;
        RegionSelectedLabel.IsVisible = true;
    }

    // ── Constellation search ──────────────────────────────────────────────────

    private async void OnConstSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _selectedConstId = null;
        _selectedConstName = "";
        ConstSelectedLabel.IsVisible = false;
        ConstResultsBorder.IsVisible = false;

        var text = ConstSearchBox.Text ?? "";
        if (text.Length < 2) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            await Task.Delay(250, ct);
            var results = await _service.SearchSdeConstellationsAsync(text, ct);
            if (ct.IsCancellationRequested) return;
            ConstResultsList.ItemsSource = results;
            ConstResultsBorder.IsVisible = results.Count > 0;
        }
        catch (Exception) { }
    }

    private void OnConstSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ConstResultsList.SelectedItem is not SdeConstellationResult r) return;
        _selectedConstId   = r.ConstellationId;
        _selectedConstName = r.Name;
        ConstResultsBorder.IsVisible = false;
        ConstSelectedLabel.Text      = r.Name;
        ConstSelectedLabel.IsVisible = true;
    }

    // ── Save / Cancel ─────────────────────────────────────────────────────────

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        ValidationHint.IsVisible = false;
        var isDeliver = TypeCombo.SelectedIndex == 0;

        if (isDeliver)
        {
            if (_selectedTypeId is null)
            {
                ShowValidation("Please select an item type.");
                return;
            }
            if (_selectedStationId is null)
            {
                ShowValidation("Please select a destination station or structure.");
                return;
            }
            Close(new CorpStandingProject
            {
                ProjectType  = "deliver_item",
                ItemTypeId   = _selectedTypeId,
                ItemTypeName = _selectedTypeName,
                StationId    = _selectedStationId,
                StationName  = _selectedStationName,
            });
        }
        else
        {
            if (ScopeSystem.IsChecked == true)
            {
                if (_selectedSystemId is null)
                {
                    ShowValidation("Please select a solar system.");
                    return;
                }
                Close(new CorpStandingProject
                {
                    ProjectType     = "destroy_npc",
                    ScopeType       = "system",
                    SolarSystemId   = _selectedSystemId,
                    SolarSystemName = _selectedSystemName,
                });
            }
            else if (ScopeRegionAdm.IsChecked == true)
            {
                if (_selectedRegionId is null)
                {
                    ShowValidation("Please select a region.");
                    return;
                }
                Close(new CorpStandingProject
                {
                    ProjectType     = "destroy_npc",
                    ScopeType       = "region_adm",
                    ScopeEntityId   = _selectedRegionId,
                    ScopeEntityName = _selectedRegionName,
                    MinAdm          = (double)(RegionAdmBox.Value ?? 4.0m),
                });
            }
            else if (ScopeAllianceSov.IsChecked == true)
            {
                if (AllianceCombo.SelectedItem is not AllianceRow alliance)
                {
                    ShowValidation("Please select an alliance.");
                    return;
                }
                Close(new CorpStandingProject
                {
                    ProjectType     = "destroy_npc",
                    ScopeType       = "alliance_sov",
                    // ⚠️ Stored in ScopeEntityId like the other scoped types. Alliance ids are
                    // well inside int range, and giving this scope its own column would leave
                    // three places to look for "what is this project scoped to".
                    ScopeEntityId   = (int)alliance.Id,
                    ScopeEntityName = alliance.Name,
                });
            }
            else
            {
                if (_selectedConstId is null)
                {
                    ShowValidation("Please select a constellation.");
                    return;
                }
                Close(new CorpStandingProject
                {
                    ProjectType     = "destroy_npc",
                    ScopeType       = "constellation_adm",
                    ScopeEntityId   = _selectedConstId,
                    ScopeEntityName = _selectedConstName,
                    MinAdm          = (double)(ConstAdmBox.Value ?? 4.0m),
                });
            }
        }
    }

    private void ShowValidation(string msg)
    {
        ValidationHint.Text      = msg;
        ValidationHint.IsVisible = true;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}

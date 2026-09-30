using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Localization;

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

    /// <summary>
    /// A search result as a list shows it: the name in the interface language, which the item
    /// template binds, over the result. ⚠️ The result keeps the English, and that is what the
    /// project saves — it is also what the posted report prints.
    /// </summary>
    private sealed record Shown<T>(T Result, string Name);

    /// <summary>Results named, and listed, as the screen shows them.</summary>
    private static List<Shown<T>> ShownAll<T>(IEnumerable<T> results, Func<T, string> name) =>
        [.. results.Select(r => new Shown<T>(r, name(r))).OrderBy(s => s.Name, StringComparer.CurrentCulture)];

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
                ItemSelectedLabel.Text      = SdeNames.Type(p.ItemTypeId.Value, p.ItemTypeName);
                ItemSelectedLabel.IsVisible = true;
            }
            if (p.StationId.HasValue)
            {
                _selectedStationId          = p.StationId;
                _selectedStationName        = p.StationName;
                StationSelectedLabel.Text   = SdeNames.Location(p.StationId.Value, p.StationName);
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
                    RegionSelectedLabel.Text      = SdeNames.Region(p.ScopeEntityId.Value, p.ScopeEntityName);
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
                    ConstSelectedLabel.Text      = SdeNames.Constellation(p.ScopeEntityId.Value, p.ScopeEntityName);
                    ConstSelectedLabel.IsVisible = true;
                }
                ConstAdmBox.Value = (decimal)(p.MinAdm ?? 4.0);
                break;
            case "alliance_sov":
                ScopeAllianceSov.IsChecked = true;
                AllianceAdmBox.Value = (decimal)(p.MinAdm ?? 4.0);
                // The combo is filled asynchronously; LoadAlliancesAsync re-selects this id when
                // the rows arrive.
                break;
            default:
                ScopeSystem.IsChecked = true;
                if (p.SolarSystemId.HasValue)
                {
                    _selectedSystemId             = p.SolarSystemId;
                    _selectedSystemName           = p.SolarSystemName;
                    SystemSelectedLabel.Text      = SdeNames.SolarSystem(p.SolarSystemId.Value, p.SolarSystemName);
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

    // ⚠️ Defers to UpdateScopePanels rather than repeating it. This used to set the three panels
    // itself, so adding a fourth scope showed its radio, hid the others, and displayed nothing:
    // the panel it should have shown was only known to the other copy.
    private void OnScopeChanged(object? sender, RoutedEventArgs e) => UpdateScopePanels();

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
            await SdeNames.EnsureLoadedAsync(ct);
            if (ct.IsCancellationRequested) return;
            ItemResultsList.ItemsSource = ShownAll(results, r => SdeNames.Type(r.TypeId, r.Name));
            ItemResultsBorder.IsVisible = results.Count > 0;
        }
        catch (Exception) { }
    }

    private void OnItemSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ItemResultsList.SelectedItem is not Shown<SdeTypeResult> { Result: var r } shown) return;
        _selectedTypeId   = r.TypeId;
        _selectedTypeName = r.Name;
        ItemResultsBorder.IsVisible = false;
        ItemSelectedLabel.Text      = shown.Name;
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
            StationResultsList.ItemsSource = ShownAll(results, r => r.DisplayName);
            StationResultsBorder.IsVisible = results.Count > 0;
        }
        catch (Exception) { }
    }

    private void OnStationSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (StationResultsList.SelectedItem is not Shown<SdeStationResult> { Result: var r } shown) return;
        _selectedStationId   = r.StationId;
        _selectedStationName = r.Name;
        StationResultsBorder.IsVisible = false;
        StationSelectedLabel.Text      = shown.Name;
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
            await SdeNames.EnsureLoadedAsync(ct);
            if (ct.IsCancellationRequested) return;
            SystemResultsList.ItemsSource = ShownAll(results, r => SdeNames.SolarSystem(r.SystemId, r.Name));
            SystemResultsBorder.IsVisible = results.Count > 0;
        }
        catch (Exception) { }
    }

    private void OnSystemSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (SystemResultsList.SelectedItem is not Shown<SdeSystemResult> { Result: var r } shown) return;
        _selectedSystemId   = r.SystemId;
        _selectedSystemName = r.Name;
        SystemResultsBorder.IsVisible = false;
        SystemSelectedLabel.Text      = shown.Name;
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
            await SdeNames.EnsureLoadedAsync(ct);
            if (ct.IsCancellationRequested) return;
            RegionResultsList.ItemsSource = ShownAll(results, r => SdeNames.Region(r.RegionId, r.Name));
            RegionResultsBorder.IsVisible = results.Count > 0;
        }
        catch (Exception) { }
    }

    private void OnRegionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (RegionResultsList.SelectedItem is not Shown<SdeRegionResult> { Result: var r } shown) return;
        _selectedRegionId   = r.RegionId;
        _selectedRegionName = r.Name;
        RegionResultsBorder.IsVisible = false;
        RegionSelectedLabel.Text      = shown.Name;
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
            await SdeNames.EnsureLoadedAsync(ct);
            if (ct.IsCancellationRequested) return;
            ConstResultsList.ItemsSource = ShownAll(results, r => SdeNames.Constellation(r.ConstellationId, r.Name));
            ConstResultsBorder.IsVisible = results.Count > 0;
        }
        catch (Exception) { }
    }

    private void OnConstSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ConstResultsList.SelectedItem is not Shown<SdeConstellationResult> { Result: var r } shown) return;
        _selectedConstId   = r.ConstellationId;
        _selectedConstName = r.Name;
        ConstResultsBorder.IsVisible = false;
        ConstSelectedLabel.Text      = shown.Name;
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
                ShowValidation(CorpText.ErrSelectItemType);
                return;
            }
            if (_selectedStationId is null)
            {
                ShowValidation(CorpText.ErrSelectDestination);
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
                    ShowValidation(CorpText.ErrSelectSolarSystem);
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
                    ShowValidation(CorpText.ErrSelectRegion);
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
                    ShowValidation(CorpText.ErrSelectAlliance);
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
                    MinAdm          = (double)(AllianceAdmBox.Value ?? 4.0m),
                });
            }
            else
            {
                if (_selectedConstId is null)
                {
                    ShowValidation(CorpText.ErrSelectConstellation);
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

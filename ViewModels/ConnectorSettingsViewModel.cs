using System.Collections.ObjectModel;
using System.Reactive;
using EveConsole.Localization;
using EveConsole.Services;
using EveConsole.Services.Connector;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Settings → Connectors: the connectors the user runs (or their corporations host), each used for the
/// corporations, alliances and functions the user picks. Covers every client attached to this database.
/// The selected one is set up in <see cref="ConnectorEntryViewModel"/>.
/// </summary>
public class ConnectorSettingsViewModel : ReactiveObject
{
    private readonly ConnectorService     _connector;
    private readonly LinkedCharacterStore _store;
    private readonly SemaphoreSlim        _loadGate = new(1, 1);

    public ConnectorSettingsViewModel(ConnectorService connector, LinkedCharacterStore store)
    {
        _connector = connector;
        _store     = store;

        // Another client, the background refresh, or this page: an entry added, connected, disconnected or
        // chosen for something else, or links stored. An open page follows.
        _connector.Changed += Follow;
        _store.Changed     += Follow;

        AddCommand    = ReactiveCommand.CreateFromTask(AddAsync);
        RemoveCommand = ReactiveCommand.CreateFromTask(RemoveAsync, this.WhenAnyValue(x => x.CanRemove));
    }

    public ReactiveCommand<Unit, Unit> AddCommand    { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    public ObservableCollection<ConnectorEntryViewModel> Entries { get; } = [];

    private ConnectorEntryViewModel? _selectedEntry;
    public ConnectorEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set { this.RaiseAndSetIfChanged(ref _selectedEntry, value); this.RaisePropertyChanged(nameof(CanRemove)); }
    }

    public bool CanRemove => SelectedEntry is { IsBusy: false };

    private string _listStatus = "";
    public string ListStatus { get => _listStatus; private set => this.RaiseAndSetIfChanged(ref _listStatus, value); }

    private void Follow() => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LoadAsync());

    /// <summary>Reads the connectors and what is stored again: the window opening, and after a change. A
    /// connector set up from another client on this database turns up here. Asks no connector anything.</summary>
    public async Task LoadAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            var snaps   = await _connector.ListAsync();
            var scopes  = await _connector.ScopeChoicesAsync();
            var sources = (await _store.SourcesAsync()).ToDictionary(s => s.Source);
            var choices = ConnectorEntryViewModel.ChoicesKey(snaps);
            var selectedId = SelectedEntry?.Id;

            foreach (var gone in Entries.Where(e => snaps.All(s => s.Id != e.Id)).ToList())
                Entries.Remove(gone);

            for (var i = 0; i < snaps.Count; i++)
            {
                var snap  = snaps[i];
                var entry = Entries.FirstOrDefault(e => e.Id == snap.Id);
                if (entry is null)
                {
                    entry = new ConnectorEntryViewModel(_connector, snap.Id, LoadAsync, () => this.RaisePropertyChanged(nameof(CanRemove)));
                    Entries.Insert(Math.Min(i, Entries.Count), entry);
                }
                entry.Apply(snap, scopes, sources.GetValueOrDefault(LinkedCharacterStore.SourceKey(snap.Id)), _connector.LastResult(snap.Id), choices);
            }

            // The selection kept by id; otherwise the first, so the page never opens on nothing.
            SelectedEntry = Entries.FirstOrDefault(e => e.Id == selectedId) ?? Entries.FirstOrDefault();
            ListStatus = "";
        }
        catch (Exception ex)
        {
            // Nobody awaits this when the window opens, so the failure has to say so itself.
            ListStatus = string.Format(CommonText.ErrorWithMessage, ex.Message);
        }
        finally { _loadGate.Release(); }
    }

    private async Task AddAsync()
    {
        try
        {
            var id = await _connector.AddAsync();
            await LoadAsync();
            SelectedEntry = Entries.FirstOrDefault(e => e.Id == id) ?? SelectedEntry;
        }
        catch (Exception ex)
        {
            ListStatus = string.Format(CommonText.ErrorWithMessage, ex.Message);
        }
    }

    /// <summary>Asks the user to confirm (title, message); set by the window. Without it, nothing is asked.</summary>
    public Func<string, string, Task<bool>>? ShowConfirmDialog { get; set; }

    private async Task RemoveAsync()
    {
        if (SelectedEntry is not { } entry) return;
        var index = Entries.IndexOf(entry);
        try
        {
            // ⚠️ Not undone: the key ends at the connector, and the choices and the links it gave go. Asked only
            // when there is something to lose beyond a blank or disconnected entry's address and choices.
            if ((entry.IsConnected || entry.HasStoredLinks) && ShowConfirmDialog is not null
                && !await ShowConfirmDialog(SettingsText.ConnectorRemoveTitle, string.Format(SettingsText.ConnectorRemoveConfirm, entry.Title)))
                return;

            await _connector.RemoveAsync(entry.Id);
            await LoadAsync();
            SelectedEntry = Entries.Count == 0 ? null : Entries[Math.Clamp(index, 0, Entries.Count - 1)];
        }
        catch (Exception ex)
        {
            ListStatus = string.Format(CommonText.ErrorWithMessage, ex.Message);
        }
    }
}

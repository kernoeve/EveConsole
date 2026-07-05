using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Avalonia.Threading;
using EveCortex.Data;
using EveCortex.Models;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

// Live per-region row for the price-history sweep monitor.
public class RegionStatusRowVm : ReactiveObject
{
    public int    RegionId   { get; }
    public string RegionName { get; }

    public RegionStatusRowVm(int regionId, string name) { RegionId = regionId; RegionName = name; }

    private int _refreshed;
    public int Refreshed
    {
        get => _refreshed;
        set { this.RaiseAndSetIfChanged(ref _refreshed, value); this.RaisePropertyChanged(nameof(Summary)); }
    }

    private int _queue;
    public int Queue
    {
        get => _queue;
        set { this.RaiseAndSetIfChanged(ref _queue, value); this.RaisePropertyChanged(nameof(Summary)); }
    }

    public int Total => Refreshed + Queue;
    public string Summary => $"{Refreshed:N0} / {Total:N0} refreshed (24h)  ·  {Queue:N0} queued";
}

public class PriceHistorySettingsViewModel : ReactiveObject
{
    private readonly AppDbContext         _db;
    private readonly MarketHistoryService _history;
    private readonly DispatcherTimer      _statusTimer;
    private int _statusTick;

    public ObservableCollection<PriceHistoryRegion> ConfiguredRegions { get; } = [];
    public ObservableCollection<RegionStatusRowVm>  RegionStatuses    { get; } = [];

    private IReadOnlyList<SdeRegionOption> _allRegions = [];
    public IReadOnlyList<SdeRegionOption> AllRegions
    {
        get => _allRegions;
        private set => this.RaiseAndSetIfChanged(ref _allRegions, value);
    }

    private SdeRegionOption? _selectedNewRegion;
    public SdeRegionOption? SelectedNewRegion
    {
        get => _selectedNewRegion;
        set => this.RaiseAndSetIfChanged(ref _selectedNewRegion, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> AddCommand    { get; }
    public ReactiveCommand<PriceHistoryRegion, System.Reactive.Unit>   RemoveCommand { get; }

    public PriceHistorySettingsViewModel(AppDbContext db, MarketHistoryService history)
    {
        _db      = db;
        _history = history;

        var canAdd = this.WhenAnyValue(x => x.SelectedNewRegion)
                         .Select(r => r is not null);
        AddCommand    = ReactiveCommand.CreateFromTask(AddRegionAsync, canAdd);
        RemoveCommand = ReactiveCommand.CreateFromTask<PriceHistoryRegion>(RemoveRegionAsync);

        _ = LoadAsync();

        // Live sweep monitor: copy the service's in-memory per-region counts every 2s
        // (cheap), and recompute the accurate counts from the DB every ~10s.
        _ = RefreshAndSyncAsync();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += async (_, _) =>
        {
            try
            {
                if (++_statusTick % 5 == 0) await _history.RefreshStatusesAsync();
                SyncStatuses();
            }
            catch { /* best-effort monitor; never crash the UI timer */ }
        };
        _statusTimer.Start();
    }

    private async Task RefreshAndSyncAsync()
    {
        try { await _history.RefreshStatusesAsync(); } catch { /* best-effort monitor */ }
        SyncStatuses();
    }

    private void SyncStatuses()
    {
        var snap = _history.SweepStatuses;
        foreach (var s in snap)
        {
            var row = RegionStatuses.FirstOrDefault(r => r.RegionId == s.RegionId);
            if (row is null)
            {
                row = new RegionStatusRowVm(s.RegionId, s.RegionName);
                RegionStatuses.Add(row);
            }
            row.Refreshed = s.Refreshed;
            row.Queue     = s.Queue;
        }
        foreach (var row in RegionStatuses.Where(r => snap.All(s => s.RegionId != r.RegionId)).ToList())
            RegionStatuses.Remove(row);
    }

    private async Task LoadAsync()
    {
        var all = await _db.SdeRegions.AsNoTracking()
            .Where(r => !r.IsWormhole)
            .OrderBy(r => r.Name)
            .Select(r => new SdeRegionOption(r.RegionId, r.Name))
            .ToListAsync();

        // Fallback seed (startup normally does this): The Forge and Domain.
        if (!await _db.PriceHistoryRegions.AnyAsync())
        {
            _db.PriceHistoryRegions.Add(new PriceHistoryRegion { RegionId = 10000002, RegionName = "The Forge" });
            _db.PriceHistoryRegions.Add(new PriceHistoryRegion { RegionId = 10000043, RegionName = "Domain" });
            await _db.SaveChangesAsync();
        }

        var configured = await _db.PriceHistoryRegions
            .OrderBy(r => r.RegionName).ToListAsync();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            AllRegions = all;
            ConfiguredRegions.Clear();
            foreach (var r in configured) ConfiguredRegions.Add(r);
        });
    }

    private async Task AddRegionAsync()
    {
        if (SelectedNewRegion is null) return;
        if (ConfiguredRegions.Any(r => r.RegionId == SelectedNewRegion.RegionId)) return;

        var entry = new PriceHistoryRegion
            { RegionId = SelectedNewRegion.RegionId, RegionName = SelectedNewRegion.Name };

        _db.PriceHistoryRegions.Add(entry);
        await _db.SaveChangesAsync();

        ConfiguredRegions.Add(entry);
    }

    private async Task RemoveRegionAsync(PriceHistoryRegion region)
    {
        _db.PriceHistoryRegions.Remove(
            _db.PriceHistoryRegions.Local.First(r => r.RegionId == region.RegionId));
        await _db.SaveChangesAsync();

        ConfiguredRegions.Remove(region);
    }
}

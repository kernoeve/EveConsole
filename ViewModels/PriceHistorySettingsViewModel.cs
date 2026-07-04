using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Avalonia.Threading;
using EveCortex.Data;
using EveCortex.Models;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

public class PriceHistorySettingsViewModel : ReactiveObject
{
    private readonly AppDbContext _db;

    public ObservableCollection<PriceHistoryRegion> ConfiguredRegions { get; } = [];

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

    public PriceHistorySettingsViewModel(AppDbContext db)
    {
        _db = db;

        var canAdd = this.WhenAnyValue(x => x.SelectedNewRegion)
                         .Select(r => r is not null);
        AddCommand    = ReactiveCommand.CreateFromTask(AddRegionAsync, canAdd);
        RemoveCommand = ReactiveCommand.CreateFromTask<PriceHistoryRegion>(RemoveRegionAsync);

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var all = await _db.SdeRegions.AsNoTracking()
            .Where(r => !r.IsWormhole)
            .OrderBy(r => r.Name)
            .Select(r => new SdeRegionOption(r.RegionId, r.Name))
            .ToListAsync();

        if (!await _db.PriceHistoryRegions.AnyAsync())
        {
            _db.PriceHistoryRegions.Add(new PriceHistoryRegion
                { RegionId = 10000002, RegionName = "The Forge" });
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

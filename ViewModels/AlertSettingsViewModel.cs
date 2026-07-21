using System.Reactive;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

public class AlertSettingsViewModel : ReactiveObject
{
    private readonly AppDbContext _db;

    private bool    _skillQueueEmpty       = true;
    private bool    _skillQueuePaused      = true;
    private bool    _skillQueueEmptyInDays = true;
    private decimal _skillQueueEmptyDays   = 30;
    private bool    _assetSafety                = true;
    private bool    _inactiveStandingProjects   = true;
    private string  _status                    = "";

    public bool SkillQueueEmpty
    {
        get => _skillQueueEmpty;
        set => this.RaiseAndSetIfChanged(ref _skillQueueEmpty, value);
    }

    public bool SkillQueuePaused
    {
        get => _skillQueuePaused;
        set => this.RaiseAndSetIfChanged(ref _skillQueuePaused, value);
    }

    public bool SkillQueueEmptyInDays
    {
        get => _skillQueueEmptyInDays;
        set => this.RaiseAndSetIfChanged(ref _skillQueueEmptyInDays, value);
    }

    // decimal so Avalonia NumericUpDown binds without a converter
    public decimal SkillQueueEmptyDays
    {
        get => _skillQueueEmptyDays;
        set => this.RaiseAndSetIfChanged(ref _skillQueueEmptyDays, value);
    }

    public bool AssetSafety
    {
        get => _assetSafety;
        set => this.RaiseAndSetIfChanged(ref _assetSafety, value);
    }

    public bool InactiveStandingProjects
    {
        get => _inactiveStandingProjects;
        set => this.RaiseAndSetIfChanged(ref _inactiveStandingProjects, value);
    }

    public string Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    public AlertSettingsViewModel(AppDbContext db)
    {
        _db = db;
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
    }

    public async Task LoadAsync()
    {
        var s = await _db.AlertSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == 1);
        if (s is null) return;
        SkillQueueEmpty       = s.SkillQueueEmpty;
        SkillQueuePaused      = s.SkillQueuePaused;
        SkillQueueEmptyInDays = s.SkillQueueEmptyInDays;
        SkillQueueEmptyDays   = s.SkillQueueEmptyDays;
        AssetSafety                = s.AssetSafety;
        InactiveStandingProjects   = s.InactiveStandingProjects;
    }

    private async Task SaveAsync()
    {
        int days      = (int)Math.Clamp(SkillQueueEmptyDays, 1, 365);
        int empty     = SkillQueueEmpty             ? 1 : 0;
        int paused    = SkillQueuePaused            ? 1 : 0;
        int emptyDay  = SkillQueueEmptyInDays       ? 1 : 0;
        int safety    = AssetSafety                 ? 1 : 0;
        int inactive  = InactiveStandingProjects    ? 1 : 0;

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AlertSettings"
                ("Id","SkillQueueEmpty","SkillQueuePaused","SkillQueueEmptyInDays","SkillQueueEmptyDays","AssetSafety","InactiveStandingProjects")
            VALUES (1,{empty},{paused},{emptyDay},{days},{safety},{inactive})
            ON CONFLICT("Id") DO UPDATE SET
                "SkillQueueEmpty"             = excluded."SkillQueueEmpty",
                "SkillQueuePaused"            = excluded."SkillQueuePaused",
                "SkillQueueEmptyInDays"       = excluded."SkillQueueEmptyInDays",
                "SkillQueueEmptyDays"         = excluded."SkillQueueEmptyDays",
                "AssetSafety"                 = excluded."AssetSafety",
                "InactiveStandingProjects"    = excluded."InactiveStandingProjects"
            """);

        Status = "Saved.";
        await Task.Delay(2000);
        Status = "";
    }
}

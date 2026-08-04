using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using EveConsole.Alarms;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One row in the alarm list.</summary>
public sealed class AlarmRowVm : ReactiveObject
{
    public required long   Id          { get; init; }
    public required string Name        { get; init; }
    public required string Condition   { get; init; }
    public required string CreatedBy   { get; init; }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set => this.RaiseAndSetIfChanged(ref _enabled, value);
    }

    public required int             FireCount   { get; init; }
    public required DateTimeOffset? LastFiredAt { get; init; }
    public string?                  Error       { get; init; }

    public string StatusText => !Enabled       ? "Disabled"
                              : Error is not null ? "Error"
                              : FireCount > 0  ? $"Armed · fired {FireCount}×"
                                               : "Armed";

    public string LastFiredText => LastFiredAt is { } t
        ? t.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture)
        : "—";

    public bool IsAgentCreated => string.Equals(CreatedBy, "agent", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A single form field derived from a condition's JSON Schema, so a new condition type gets a
/// usable editor without any new XAML.
/// </summary>
public sealed class AlarmFieldVm : ReactiveObject
{
    public required string  Name        { get; init; }
    public required string  Label       { get; init; }
    public required string  Kind        { get; init; }  // string | integer | boolean | datetime | enum
    public          string? Description { get; init; }
    public          bool    Required    { get; init; }
    public IReadOnlyList<string>? Options { get; init; }

    private string _text = "";
    public string Text { get => _text; set => this.RaiseAndSetIfChanged(ref _text, value); }

    private bool _flag;
    public bool Flag { get => _flag; set => this.RaiseAndSetIfChanged(ref _flag, value); }

    private DateTimeOffset? _date;
    public DateTimeOffset? Date { get => _date; set => this.RaiseAndSetIfChanged(ref _date, value); }

    private TimeSpan _time;
    public TimeSpan Time { get => _time; set => this.RaiseAndSetIfChanged(ref _time, value); }

    public bool IsText     => Kind is "string" or "integer";
    public bool IsBoolean  => Kind == "boolean";
    public bool IsDateTime => Kind == "datetime";
    public bool IsEnum     => Kind == "enum";

    /// <summary>A SQL field needs room to breathe; everything else is a single line.</summary>
    public bool IsMultiline => Kind == "string" && Name.Contains("sql", StringComparison.OrdinalIgnoreCase);
    public double MinHeight => IsMultiline ? 96 : 0;
}

/// <summary>One action attached to the alarm being edited.</summary>
public sealed class AlarmActionVm : ReactiveObject
{
    private readonly AlarmSoundService _sounds;

    public AlarmActionVm(AlarmSoundService sounds, AlarmActionKind kind, JsonElement cfg)
    {
        _sounds     = sounds;
        AvailableSounds = sounds.List();
        _kind       = kind;

        var soundKey = Str(cfg, "sound") ?? AlarmSoundService.DefaultKey;
        _sound       = AvailableSounds.FirstOrDefault(s => s.Key == soundKey) ?? AvailableSounds.FirstOrDefault();
        _volume      = Int(cfg, "volume") ?? 100;
        _title       = Str(cfg, "title") ?? "";
        _body        = Str(cfg, "body") ?? Str(cfg, "message") ?? "";
        _instruction = Str(cfg, "instruction") ?? "";

        PreviewCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (Sound is { } s) await _sounds.PlayAsync(s.Key, Volume);
        });
    }

    private AlarmActionKind _kind;
    public AlarmActionKind Kind
    {
        get => _kind;
        set
        {
            this.RaiseAndSetIfChanged(ref _kind, value);
            this.RaisePropertyChanged(nameof(IsSound));
            this.RaisePropertyChanged(nameof(IsAgent));
            this.RaisePropertyChanged(nameof(IsAlert));
            this.RaisePropertyChanged(nameof(IsDialog));
            this.RaisePropertyChanged(nameof(HasText));
        }
    }

    public IReadOnlyList<AlarmActionKind> AvailableKinds { get; } =
        [AlarmActionKind.Sound, AlarmActionKind.AgentNotify, AlarmActionKind.Alert, AlarmActionKind.Dialog];

    public IReadOnlyList<AlarmSound> AvailableSounds { get; }

    private AlarmSound? _sound;
    public AlarmSound? Sound { get => _sound; set => this.RaiseAndSetIfChanged(ref _sound, value); }

    private int _volume;
    public int Volume { get => _volume; set => this.RaiseAndSetIfChanged(ref _volume, value); }

    private string _title;
    public string Title { get => _title; set => this.RaiseAndSetIfChanged(ref _title, value); }

    private string _body;
    public string Body { get => _body; set => this.RaiseAndSetIfChanged(ref _body, value); }

    private string _instruction;
    public string Instruction { get => _instruction; set => this.RaiseAndSetIfChanged(ref _instruction, value); }

    public bool IsSound  => Kind == AlarmActionKind.Sound;
    public bool IsAgent  => Kind == AlarmActionKind.AgentNotify;
    public bool IsAlert  => Kind == AlarmActionKind.Alert;
    public bool IsDialog => Kind == AlarmActionKind.Dialog;
    public bool HasText  => IsAlert || IsDialog;

    public ReactiveCommand<Unit, Unit> PreviewCommand { get; }

    public string ToConfigJson()
    {
        var o = new JsonObject();
        switch (Kind)
        {
            case AlarmActionKind.Sound:
                o["sound"]  = Sound?.Key ?? AlarmSoundService.DefaultKey;
                o["volume"] = Volume;
                break;
            case AlarmActionKind.AgentNotify:
                if (!string.IsNullOrWhiteSpace(Instruction)) o["instruction"] = Instruction;
                break;
            case AlarmActionKind.Alert:
                if (!string.IsNullOrWhiteSpace(Title)) o["title"] = Title;
                if (!string.IsNullOrWhiteSpace(Body))  o["body"]  = Body;
                break;
            case AlarmActionKind.Dialog:
                if (!string.IsNullOrWhiteSpace(Title)) o["title"]   = Title;
                if (!string.IsNullOrWhiteSpace(Body))  o["message"] = Body;
                break;
        }
        return o.ToJsonString();
    }

    private static string? Str(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var p)
        && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var p)
        && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;
}

/// <summary>A past firing, shown as history.</summary>
public sealed class AlarmEventVm
{
    public required DateTimeOffset FiredAt    { get; init; }
    public required string         AlarmName  { get; init; }
    public required string         Summary    { get; init; }
    public required int            MatchCount { get; init; }

    public string WhenText => FiredAt.ToLocalTime().ToString("d MMM HH:mm:ss", CultureInfo.CurrentCulture);
}

/// <summary>An outstanding alert raised by the Alert action.</summary>
public sealed class AlarmAlertVm : ReactiveObject
{
    public required long           Id        { get; init; }
    public required string         Title     { get; init; }
    public required string?        Body      { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public string WhenText => CreatedAt.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);

    public ReactiveCommand<Unit, Unit>? DismissCommand { get; init; }
}

public sealed class AlarmsViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AlarmService                    _service;
    private readonly AlarmSoundService               _sounds;

    public AlarmsViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        AlarmService                    service,
        AlarmSoundService               sounds)
    {
        _dbFactory = dbFactory;
        _service   = service;
        _sounds    = sounds;

        Conditions = service.Registry.All.ToList();

        NewCommand    = ReactiveCommand.Create(NewAlarm);
        SaveCommand   = ReactiveCommand.CreateFromTask(SaveAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        TestFireCommand = ReactiveCommand.CreateFromTask(TestFireAsync);

        // A firing while the tool is open should show up without the user hunting for Refresh.
        service.Fired += OnFired;

        this.WhenAnyValue(x => x.SelectedAlarm)
            .Where(a => a is not null)
            .Subscribe(a => _ = LoadEditorAsync(a!.Id));

        this.WhenAnyValue(x => x.SelectedCondition)
            .Subscribe(_ => RebuildFields());
    }

    private void OnFired() => Dispatcher.UIThread.Post(() => _ = LoadAsync());

    public IReadOnlyList<IAlarmCondition> Conditions { get; }

    public ObservableCollection<AlarmRowVm>    Alarms  { get; } = [];
    public ObservableCollection<AlarmEventVm>  History { get; } = [];
    public ObservableCollection<AlarmAlertVm>  Alerts  { get; } = [];
    public ObservableCollection<AlarmFieldVm>  Fields  { get; } = [];
    public ObservableCollection<AlarmActionVm> Actions { get; } = [];

    public IReadOnlyList<AlarmRepeat> RepeatModes { get; } = [AlarmRepeat.Continuous, AlarmRepeat.OneShot];

    private AlarmRowVm? _selectedAlarm;
    public AlarmRowVm? SelectedAlarm
    {
        get => _selectedAlarm;
        set => this.RaiseAndSetIfChanged(ref _selectedAlarm, value);
    }

    // ── Editor state ─────────────────────────────────────────────────────────
    private long _editingId;
    public long EditingId { get => _editingId; private set => this.RaiseAndSetIfChanged(ref _editingId, value); }

    private string _name = "";
    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }

    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => this.RaiseAndSetIfChanged(ref _enabled, value); }

    private IAlarmCondition? _selectedCondition;
    public IAlarmCondition? SelectedCondition
    {
        get => _selectedCondition;
        set => this.RaiseAndSetIfChanged(ref _selectedCondition, value);
    }

    private AlarmRepeat _repeat = AlarmRepeat.Continuous;
    public AlarmRepeat Repeat { get => _repeat; set => this.RaiseAndSetIfChanged(ref _repeat, value); }

    private int _pollSeconds = 60;
    public int PollSeconds { get => _pollSeconds; set => this.RaiseAndSetIfChanged(ref _pollSeconds, value); }

    private int _cooldownSeconds;
    public int CooldownSeconds { get => _cooldownSeconds; set => this.RaiseAndSetIfChanged(ref _cooldownSeconds, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private bool _hasEditor;
    public bool HasEditor { get => _hasEditor; private set => this.RaiseAndSetIfChanged(ref _hasEditor, value); }

    private bool _hasAlerts;
    public bool HasAlerts { get => _hasAlerts; private set => this.RaiseAndSetIfChanged(ref _hasAlerts, value); }

    public AlarmService Service => _service;

    public ReactiveCommand<Unit, Unit> NewCommand      { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand     { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand   { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand  { get; }
    public ReactiveCommand<Unit, Unit> TestFireCommand { get; }

    public ReactiveCommand<Unit, Unit> AddActionCommand => ReactiveCommand.Create(() =>
        Actions.Add(new AlarmActionVm(_sounds, AlarmActionKind.Sound, default)));

    public ReactiveCommand<AlarmActionVm, Unit> RemoveActionCommand =>
        ReactiveCommand.Create<AlarmActionVm>(a => Actions.Remove(a));

    // ── Loading ──────────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        // SQLite has no real async I/O — awaiting it on the UI thread still blocks the UI.
        var (rows, events, alerts) = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var alarms = await db.Alarms.AsNoTracking().OrderBy(a => a.Name).ToListAsync();

            var evts = await db.AlarmEvents.AsNoTracking()
                .OrderByDescending(e => e.Id).Take(200).ToListAsync();

            var alts = await db.AlarmAlerts.AsNoTracking()
                .Where(a => !a.Dismissed)
                .OrderByDescending(a => a.Id).Take(200).ToListAsync();

            return (alarms, evts, alts);
        });

        var byId = rows.ToDictionary(a => a.Id);

        Alarms.Clear();
        foreach (var a in rows)
        {
            var cond = _service.Registry.Find(a.ConditionType);
            var desc = "—";
            try
            {
                if (cond is not null)
                    desc = cond.Describe(JsonDocument.Parse(a.ConditionJson ?? "{}").RootElement);
            }
            catch { desc = a.ConditionType; }

            Alarms.Add(new AlarmRowVm
            {
                Id          = a.Id,
                Name        = a.Name,
                Condition   = desc,
                CreatedBy   = a.CreatedBy,
                Enabled     = a.Enabled,
                FireCount   = a.FireCount,
                LastFiredAt = a.LastFiredAt,
                Error       = a.LastError,
            });
        }

        History.Clear();
        foreach (var e in events)
            History.Add(new AlarmEventVm
            {
                FiredAt    = e.FiredAt,
                AlarmName  = byId.TryGetValue(e.AlarmId, out var al) ? al.Name : $"#{e.AlarmId}",
                Summary    = e.Summary,
                MatchCount = e.MatchCount,
            });

        Alerts.Clear();
        foreach (var a in alerts)
        {
            var id = a.Id;
            Alerts.Add(new AlarmAlertVm
            {
                Id        = id,
                Title     = a.Title,
                Body      = a.Body,
                CreatedAt = a.CreatedAt,
                DismissCommand = ReactiveCommand.CreateFromTask(() => DismissAlertAsync(id)),
            });
        }

        HasAlerts  = Alerts.Count > 0;
        StatusText = $"{Alarms.Count} alarm(s) · {Alerts.Count} open alert(s)";
    }

    private async Task DismissAlertAsync(long id)
    {
        await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "AlarmAlerts" SET "Dismissed" = 1, "DismissedAt" = {0} WHERE "Id" = {1}""",
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "+00:00",
                id);
        });

        if (Alerts.FirstOrDefault(a => a.Id == id) is { } row) Alerts.Remove(row);
        HasAlerts  = Alerts.Count > 0;
        StatusText = $"{Alarms.Count} alarm(s) · {Alerts.Count} open alert(s)";
    }

    // ── Editor ───────────────────────────────────────────────────────────────

    private void NewAlarm()
    {
        SelectedAlarm     = null;
        EditingId         = 0;
        Name              = "New alarm";
        Enabled           = true;
        Repeat            = AlarmRepeat.Continuous;
        PollSeconds       = 60;
        CooldownSeconds   = 0;
        SelectedCondition = Conditions.FirstOrDefault();
        RebuildFields();

        Actions.Clear();
        Actions.Add(new AlarmActionVm(_sounds, AlarmActionKind.Sound, default));
        HasEditor = true;
    }

    private async Task LoadEditorAsync(long id)
    {
        var (alarm, actions) = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var a  = await db.Alarms.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            var ac = await db.AlarmActions.AsNoTracking()
                .Where(x => x.AlarmId == id).OrderBy(x => x.Ordinal).ToListAsync();
            return (a, ac);
        });

        if (alarm is null) return;

        EditingId       = alarm.Id;
        Name            = alarm.Name;
        Enabled         = alarm.Enabled;
        Repeat          = alarm.Repeat;
        PollSeconds     = alarm.PollSeconds;
        CooldownSeconds = alarm.CooldownSeconds;

        // Setting this fires RebuildFields via the subscription, which clears any prior values;
        // the config is applied afterwards so it survives.
        SelectedCondition = _service.Registry.Find(alarm.ConditionType) ?? Conditions.FirstOrDefault();
        RebuildFields();

        try { ApplyConfig(JsonDocument.Parse(alarm.ConditionJson ?? "{}").RootElement); }
        catch { /* a malformed blob just leaves the fields empty */ }

        Actions.Clear();
        foreach (var a in actions)
        {
            JsonElement cfg;
            try { cfg = JsonDocument.Parse(a.ConfigJson ?? "{}").RootElement.Clone(); }
            catch { cfg = default; }
            Actions.Add(new AlarmActionVm(_sounds, a.Kind, cfg));
        }

        HasEditor = true;
    }

    /// <summary>
    /// Turns the selected condition's JSON Schema into form fields. Keeps the editor generic:
    /// a condition added later gets a working UI from its schema alone.
    /// </summary>
    private void RebuildFields()
    {
        Fields.Clear();
        if (SelectedCondition is null) return;

        JsonElement schema;
        try
        {
            schema = JsonSerializer.SerializeToElement(SelectedCondition.ParameterSchema);
        }
        catch { return; }

        if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return;

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            foreach (var r in req.EnumerateArray())
                if (r.GetString() is { } s) required.Add(s);

        foreach (var prop in props.EnumerateObject())
        {
            var spec = prop.Value;
            var type = spec.TryGetProperty("type", out var t) ? t.GetString() ?? "string" : "string";
            var desc = spec.TryGetProperty("description", out var d) ? d.GetString() : null;

            string[]? options = null;
            if (spec.TryGetProperty("enum", out var en) && en.ValueKind == JsonValueKind.Array)
                options = en.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();

            var format = spec.TryGetProperty("format", out var f) ? f.GetString() : null;

            var kind = options is not null            ? "enum"
                     : format == "date-time"          ? "datetime"
                     : type == "boolean"              ? "boolean"
                     : type is "integer" or "number"  ? "integer"
                                                      : "string";

            var field = new AlarmFieldVm
            {
                Name        = prop.Name,
                Label       = Humanise(prop.Name),
                Kind        = kind,
                Description = desc,
                Required    = required.Contains(prop.Name),
                Options     = options,
            };

            // A date-time field with nothing in it is more useful pointing at the near future
            // than at 01/01/0001 — the overwhelmingly common case is "remind me shortly".
            if (kind == "datetime")
            {
                var soon = DateTimeOffset.Now.AddMinutes(5);
                field.Date = soon.Date;
                field.Time = soon.TimeOfDay;
            }

            Fields.Add(field);
        }
    }

    private void ApplyConfig(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object) return;

        foreach (var field in Fields)
        {
            if (!config.TryGetProperty(field.Name, out var v)) continue;

            switch (field.Kind)
            {
                case "boolean":
                    field.Flag = v.ValueKind == JsonValueKind.True;
                    break;

                case "integer":
                    field.Text = v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "";
                    break;

                case "datetime":
                    if (v.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                               DateTimeStyles.AssumeLocal, out var dto))
                    {
                        var local = dto.ToLocalTime();
                        field.Date = local.Date;
                        field.Time = local.TimeOfDay;
                    }
                    break;

                default:
                    field.Text = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
                    break;
            }
        }
    }

    private string BuildConfigJson()
    {
        var o = new JsonObject();
        foreach (var field in Fields)
        {
            switch (field.Kind)
            {
                case "boolean":
                    o[field.Name] = field.Flag;
                    break;

                case "integer":
                    if (int.TryParse(field.Text, out var i)) o[field.Name] = i;
                    break;

                case "datetime":
                    if (field.Date is { } date)
                    {
                        // Compose in local time and keep the offset, so an alarm set for 18:00
                        // means 18:00 here rather than 18:00 UTC.
                        var local = new DateTimeOffset(
                            date.Year, date.Month, date.Day,
                            field.Time.Hours, field.Time.Minutes, field.Time.Seconds,
                            TimeZoneInfo.Local.GetUtcOffset(date.DateTime));
                        o[field.Name] = local.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
                    }
                    break;

                default:
                    if (!string.IsNullOrWhiteSpace(field.Text)) o[field.Name] = field.Text;
                    break;
            }
        }
        return o.ToJsonString();
    }

    private async Task SaveAsync()
    {
        if (SelectedCondition is null) { StatusText = "Pick a condition first."; return; }
        if (string.IsNullOrWhiteSpace(Name)) { StatusText = "Give the alarm a name."; return; }

        var conditionType = SelectedCondition.TypeKey;
        var conditionJson = BuildConfigJson();
        var actionRows    = Actions.Select((a, i) => (a.Kind, Json: a.ToConfigJson(), Ordinal: i)).ToList();

        var id = EditingId;
        var (savedId, wasNew) = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            Alarm alarm;
            bool isNew = id == 0;
            if (isNew)
            {
                alarm = new Alarm { CreatedAt = DateTimeOffset.Now, CreatedBy = "user" };
                db.Alarms.Add(alarm);
            }
            else
            {
                alarm = await db.Alarms.FirstAsync(a => a.Id == id);
            }

            var conditionChanged = alarm.ConditionType != conditionType
                                || alarm.ConditionJson != conditionJson;

            alarm.Name            = Name.Trim();
            alarm.Enabled         = Enabled;
            alarm.ConditionType   = conditionType;
            alarm.ConditionJson   = conditionJson;
            alarm.Repeat          = Repeat;
            alarm.PollSeconds     = Math.Max(10, PollSeconds);
            alarm.CooldownSeconds = Math.Max(0, CooldownSeconds);
            alarm.LastError       = null;

            // Changing what an alarm watches for makes its old seen-keys meaningless, so it
            // re-primes against the new condition instead of firing on the whole backlog.
            if (conditionChanged && !isNew)
            {
                alarm.Primed = false;
                await db.Database.ExecuteSqlRawAsync(
                    """DELETE FROM "AlarmSeenKeys" WHERE "AlarmId" = {0}""", id);
            }

            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync(
                """DELETE FROM "AlarmActions" WHERE "AlarmId" = {0}""", alarm.Id);

            foreach (var (kind, json, ordinal) in actionRows)
                db.AlarmActions.Add(new AlarmAction
                {
                    AlarmId    = alarm.Id,
                    Kind       = kind,
                    ConfigJson = json,
                    Ordinal    = ordinal,
                });

            await db.SaveChangesAsync();
            return (alarm.Id, isNew);
        });

        EditingId = savedId;

        // Prime here rather than letting the first tick do it, so an alarm saved moments before
        // it comes due still announces that occurrence instead of banking it as history.
        await _service.PrimeAsync(savedId);
        _service.Invalidate(savedId);

        await LoadAsync();
        SelectedAlarm = Alarms.FirstOrDefault(a => a.Id == savedId);
        StatusText    = wasNew ? "Alarm created." : "Alarm saved.";
    }

    private async Task DeleteAsync()
    {
        var id = EditingId;
        if (id == 0) return;

        await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            foreach (var sql in new[]
                     {
                         """DELETE FROM "AlarmActions"  WHERE "AlarmId" = {0}""",
                         """DELETE FROM "AlarmSeenKeys" WHERE "AlarmId" = {0}""",
                         """DELETE FROM "AlarmEvents"   WHERE "AlarmId" = {0}""",
                         """DELETE FROM "AlarmAlerts"   WHERE "AlarmId" = {0}""",
                         """DELETE FROM "Alarms"        WHERE "Id"      = {0}""",
                     })
                await db.Database.ExecuteSqlRawAsync(sql, id);
        });

        _service.Invalidate(id);
        HasEditor = false;
        EditingId = 0;
        await LoadAsync();
        StatusText = "Alarm deleted.";
    }

    /// <summary>
    /// Runs the saved alarm's actions once, without touching its condition or seen-keys — so
    /// the user can hear the chime and see the dialog without waiting for a real trigger.
    /// </summary>
    private async Task TestFireAsync()
    {
        foreach (var a in Actions)
        {
            if (a.IsSound && a.Sound is { } s) await _sounds.PlayAsync(s.Key, a.Volume);
        }
        StatusText = "Played the alarm's sounds. Save first to test the other actions.";
    }

    private static string Humanise(string name)
    {
        var words = name.Replace('_', ' ').Trim();
        return words.Length == 0 ? name : char.ToUpper(words[0]) + words[1..];
    }
}

using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using EveConsole.Localization;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// The theme editor: every theme listed, the user's own editable, the built-in ones only shown.
///
/// <para>⚠️ Saves as it goes, like every Settings tab — a colour once the picking pauses, a name
/// once the typing does — and flushes when the window closes. There is no Save button to miss.
/// Undo changes puts back what the theme was when it was opened, which is what a Save button would
/// otherwise have been protecting.</para>
///
/// <para>The preview follows every change at once; the application follows the theme it is
/// wearing when that theme is saved, so editing the theme in use repaints everything a moment
/// after the picker stops.</para>
/// </summary>
public sealed class ThemeEditorViewModel : ReactiveObject
{
    private readonly AutoSave _autoSave;

    /// <summary>The theme as it was when it was opened, for Undo changes. Null for a built-in one.</summary>
    private CustomTheme? _opened;

    public ThemeEditorViewModel()
    {
        _autoSave = new AutoSave(() => { SaveNow(); return Task.CompletedTask; },
                                 ex => Status = ex.Message);

        Groups = ThemeTokens.Groups
            .Select(g => new TokenGroupVm(GroupTitle(g.Key), g.Key == "Status" ? SettingsText.ThemeEdStatusNote : "",
                g.Tokens.Select(t => new TokenVm(t, TokenLabel(t), OnTokenChanged)).ToList()))
            .ToList();

        NewDarkCommand  = ReactiveCommand.Create(() => Create(blank: true, light: false));
        NewLightCommand = ReactiveCommand.Create(() => Create(blank: true, light: true));
        CopyCommand     = ReactiveCommand.Create(() => Create(blank: false, light: IsLight));

        var own = this.WhenAnyValue(x => x.IsOwn);
        RevertCommand   = ReactiveCommand.Create(Revert, own);
        WearCommand     = ReactiveCommand.Create(() => { if (Selected is { } s) ThemeService.Apply(s.Key); },
                                                 this.WhenAnyValue(x => x.IsWorn).Select(w => !w));
        FillCommand     = ReactiveCommand.Create(FillFromThree, own);
        DeleteCommand   = ReactiveCommand.CreateFromTask(DeleteAsync, own);

        ThemeService.Changed     += OnWorn;
        ThemeService.ListChanged += OnListChanged;

        Selected = Themes.FirstOrDefault(t => t.Key == ThemeService.Current) ?? Themes[0];
    }

    // ── The list ──────────────────────────────────────────────────────────────

    public ObservableCollection<ThemeChoice> Themes { get; } = new(ThemeService.All);

    private ThemeChoice? _selected;
    public ThemeChoice? Selected
    {
        get => _selected;
        set
        {
            // ⚠️ The theme being left is saved first, while the values on screen are still its own.
            if (value is null || value == _selected) return;
            if (_autoSave.IsPending) SaveNow();
            this.RaiseAndSetIfChanged(ref _selected, value);
            Load(value);
        }
    }

    private bool _isOwn;
    /// <summary>The user's own, and so editable. ⚠️ A built-in theme is shown but never changed.</summary>
    public bool IsOwn
    {
        get => _isOwn;
        private set { this.RaiseAndSetIfChanged(ref _isOwn, value); this.RaisePropertyChanged(nameof(IsBuiltIn)); }
    }

    public bool IsBuiltIn => !IsOwn;

    private bool _isWorn;
    public bool IsWorn { get => _isWorn; private set => this.RaiseAndSetIfChanged(ref _isWorn, value); }

    // ── The theme ─────────────────────────────────────────────────────────────

    private string _name = "";
    public string Name
    {
        get => _name;
        set
        {
            this.RaiseAndSetIfChanged(ref _name, value);
            if (IsOwn && !_autoSave.IsSuspended) _autoSave.Typed();
        }
    }

    private bool _isLight;
    /// <summary>The ground: which way the theme runs, and what it inherits.</summary>
    public bool IsLight
    {
        get => _isLight;
        set
        {
            if (value == _isLight) return;
            this.RaiseAndSetIfChanged(ref _isLight, value);
            this.RaisePropertyChanged(nameof(IsDark));
            if (IsOwn && !_autoSave.IsSuspended) { Preview(); _autoSave.Changed(); }
        }
    }

    public bool IsDark { get => !IsLight; set => IsLight = !value; }

    public IReadOnlyList<TokenGroupVm> Groups { get; }

    private IEnumerable<TokenVm> Tokens => Groups.SelectMany(g => g.Tokens);

    // ── Quick start: three colours, everything else worked out ───────────────

    private Color _startGround, _startText, _startAccent;
    public Color StartGround { get => _startGround; set => this.RaiseAndSetIfChanged(ref _startGround, value); }
    public Color StartText   { get => _startText;   set => this.RaiseAndSetIfChanged(ref _startText, value); }
    public Color StartAccent { get => _startAccent; set => this.RaiseAndSetIfChanged(ref _startAccent, value); }

    // ── Commands and status ──────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> NewDarkCommand { get; }
    public ReactiveCommand<Unit, Unit> NewLightCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> RevertCommand { get; }
    public ReactiveCommand<Unit, Unit> WearCommand { get; }
    public ReactiveCommand<Unit, Unit> FillCommand { get; }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    /// <summary>Asks the user a yes-or-no question; set by the window, which owns the dialog.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    /// <summary>
    /// The colours to preview and the ground under them, every time either changes. The view
    /// paints its preview from this rather than from the application, which is wearing whatever
    /// theme it is wearing.
    /// </summary>
    public event Action<IReadOnlyDictionary<string, Color>, bool>? PreviewChanged;

    /// <summary>The colours on screen now, for the preview.</summary>
    public Dictionary<string, Color> Colours => Tokens.ToDictionary(t => t.Key, t => t.Colour);

    /// <summary>Saves whatever is waiting; the window calls it as it closes.</summary>
    public Task FlushAsync() => _autoSave.FlushAsync();

    public void Detach()
    {
        ThemeService.Changed     -= OnWorn;
        ThemeService.ListChanged -= OnListChanged;
    }

    // ── Behaviour ─────────────────────────────────────────────────────────────

    private void Load(ThemeChoice choice)
    {
        using (_autoSave.Suspend())
        {
            var colours = CustomThemes.ColoursOf(choice.Key);
            foreach (var t in Tokens)
            {
                t.IsEditable = choice.IsOwn;
                t.Set(colours[t.Key]);
            }

            IsOwn   = choice.IsOwn;
            Name    = choice.Name;
            IsLight = CustomThemes.IsLightTheme(choice.Key);
            _opened = CustomThemes.Find(choice.Key) is { } own ? Clone(own) : null;

            StartGround = colours["SurfaceBase"];
            StartText   = colours["TextPrimary"];
            StartAccent = colours["Accent"];
        }

        IsWorn = choice.Key == ThemeService.Current;
        Status = "";
        Preview();
    }

    private void OnTokenChanged(TokenVm _)
    {
        if (_autoSave.IsSuspended) return;
        Preview();
        // ⚠️ Typed, not Changed: dragging across the picker's spectrum is a stream of colours, and
        // each saved at once would repaint the whole application for every one of them when the
        // theme is in use.
        _autoSave.Typed();
    }

    private void Preview() => PreviewChanged?.Invoke(Colours, IsLight);

    private void SaveNow()
    {
        if (Selected is not { IsOwn: true } choice) return;

        var name = string.IsNullOrWhiteSpace(Name) ? SettingsText.ThemeEdNewName : Name.Trim();
        ThemeService.SaveOwn(new CustomTheme
        {
            Key     = choice.Key,
            Name    = name,
            IsLight = IsLight,
            Colours = Tokens.ToDictionary(t => t.Key, t => Services.Colours.Hex(t.Colour)),
        });
        _autoSave.Flash(s => Status = s, SettingsText.ThemeEdSaved);
    }

    /// <summary>A new theme of the user's own: blank on a ground, or a copy of the one selected.</summary>
    private void Create(bool blank, bool light)
    {
        if (_autoSave.IsPending) SaveNow();

        Dictionary<string, Color> colours;
        string name;
        if (blank)
        {
            // ⚠️ A neutral grey on the ground's own status colours — a start that is visibly nobody
            // else's theme, ready for the three colours of the quick start.
            colours = light
                ? CustomThemes.FromThree(true,  Color.Parse("#d4d4d8"), Color.Parse("#2a2a30"), Color.Parse("#2a6fb0"))
                : CustomThemes.FromThree(false, Color.Parse("#18181c"), Color.Parse("#d4d4d8"), Color.Parse("#4f9ed8"));
            var ground = CustomThemes.ColoursOf(light ? "light" : "dark");
            foreach (var t in ThemeTokens.Groups.First(g => g.Key == "Status").Tokens) colours[t] = ground[t];
            name = Unique(SettingsText.ThemeEdNewName);
        }
        else
        {
            colours = Colours;
            name = Unique(string.Format(SettingsText.ThemeEdCopyName, Name));
        }

        var theme = new CustomTheme
        {
            Key     = CustomThemes.NewKey(),
            Name    = name,
            IsLight = light,
            Colours = colours.ToDictionary(kv => kv.Key, kv => Services.Colours.Hex(kv.Value)),
        };
        ThemeService.SaveOwn(theme);

        Selected = Themes.First(t => t.Key == theme.Key);
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { IsOwn: true } choice) return;
        if (Confirm is not null && !await Confirm(string.Format(SettingsText.ThemeEdDeleteConfirm, choice.Name))) return;

        var at = Themes.IndexOf(choice);
        using (_autoSave.Suspend()) ThemeService.DeleteOwn(choice.Key);
        _selected = null;
        Selected = Themes[Math.Min(at, Themes.Count - 1)];
    }

    private void Revert()
    {
        if (_opened is null) return;
        var colours = _opened.Colours.ToDictionary(kv => kv.Key, kv => Services.Colours.Parse(kv.Value) ?? Avalonia.Media.Colors.Magenta);
        using (_autoSave.Suspend())
        {
            foreach (var t in Tokens) t.Set(colours[t.Key]);
            Name    = _opened.Name;
            IsLight = _opened.IsLight;
        }
        Preview();
        SaveNow();
    }

    private void FillFromThree()
    {
        var made = CustomThemes.FromThree(IsLight, StartGround, StartText, StartAccent);
        using (_autoSave.Suspend())
            foreach (var t in Tokens)
                if (made.TryGetValue(t.Key, out var c)) t.Set(c);
        Preview();
        _autoSave.Changed();
    }

    private void OnWorn() => IsWorn = Selected?.Key == ThemeService.Current;

    private void OnListChanged() => ListSync.Sync(Themes, ThemeService.All, t => t.Key);

    private string Unique(string name)
    {
        var taken = ThemeService.All.Select(t => t.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
    }

    private static CustomTheme Clone(CustomTheme t) => new()
    {
        Key = t.Key, Name = t.Name, IsLight = t.IsLight, Colours = new Dictionary<string, string>(t.Colours),
    };

    private static string GroupTitle(string key) =>
        SettingsText.ResourceManager.GetString("ThemeEdGroup" + key) ?? key;

    private static string TokenLabel(string token) =>
        SettingsText.ResourceManager.GetString("ThemeToken" + token) ?? token;
}

/// <summary>One section of colours: backgrounds, lines, text, accent or status.</summary>
public sealed record TokenGroupVm(string Title, string Note, IReadOnlyList<TokenVm> Tokens)
{
    public bool HasNote => Note.Length > 0;
}

/// <summary>One colour of a theme: a swatch to pick it and a hex box to type it.</summary>
public sealed class TokenVm : ReactiveObject
{
    private readonly Action<TokenVm> _changed;

    public TokenVm(string key, string label, Action<TokenVm> changed)
    {
        Key = key; Label = label; _changed = changed;
    }

    public string Key { get; }
    public string Label { get; }

    private bool _isEditable;
    public bool IsEditable { get => _isEditable; set => this.RaiseAndSetIfChanged(ref _isEditable, value); }

    private Color _colour;
    public Color Colour
    {
        get => _colour;
        set
        {
            // The picker hands back alpha too; these are all solid.
            value = Color.FromRgb(value.R, value.G, value.B);
            if (value == _colour) return;
            this.RaiseAndSetIfChanged(ref _colour, value);
            SetHex(Services.Colours.Hex(value));
            _changed(this);
        }
    }

    private string _hex = "";
    /// <summary>The colour as typed. ⚠️ Taken only once it parses: "#c8a" half way to "#c8a84b"
    /// is a valid short colour, and taking it would jump the swatch while somebody is typing.</summary>
    public string Hex
    {
        get => _hex;
        set
        {
            this.RaiseAndSetIfChanged(ref _hex, value);
            var s = (value ?? "").Trim();
            if (s.TrimStart('#').Length == 6 && Services.Colours.Parse(s) is { } c) Colour = c;
        }
    }

    /// <summary>Sets the colour without reporting it as an edit: loading, reverting, filling.</summary>
    public void Set(Color c)
    {
        _colour = c;
        this.RaisePropertyChanged(nameof(Colour));
        SetHex(Services.Colours.Hex(c));
    }

    /// <summary>The colour as a swatch, for a built-in theme, whose colours are shown and not picked.</summary>
    public IBrush Swatch => new SolidColorBrush(_colour);

    private void SetHex(string hex)
    {
        _hex = hex;
        this.RaisePropertyChanged(nameof(Hex));
        this.RaisePropertyChanged(nameof(Swatch));
    }
}

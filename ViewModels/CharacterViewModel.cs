using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using EveCortex.Api;
using EveCortex.Auth;
using EveCortex.Data;
using EveCortex.Models;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

public record SkillQueueItemVm(
    int             QueuePosition,
    string          SkillName,
    int             FinishedLevel,
    DateTimeOffset? FinishDate);

public class CharacterViewModel : ReactiveObject
{
    private readonly EsiAuthService _auth;
    private readonly EsiClient      _esi;
    private readonly AppDbContext   _db;

    // -----------------------------------------------------------------------
    // Bindable collections
    // -----------------------------------------------------------------------

    public ObservableCollection<Character>        Characters      { get; } = [];
    public ObservableCollection<Corporation>      Corporations    { get; } = [];
    public ObservableCollection<SkillQueueItemVm> SkillQueue      { get; } = [];
    public ObservableCollection<CharacterListItem> CharacterListItems { get; } = [];
    public ObservableCollection<CorpListItem>      CorpListItems     { get; } = [];

    // -----------------------------------------------------------------------
    // Bindable properties
    // -----------------------------------------------------------------------

    private Character? _selectedCharacter;
    public Character? SelectedCharacter
    {
        get => _selectedCharacter;
        set => this.RaiseAndSetIfChanged(ref _selectedCharacter, value);
    }

    // Drives the Settings details panel; set by SelectedCharacterListItem.
    private Character? _selectedCharacterInSettings;
    public Character? SelectedCharacterInSettings
    {
        get => _selectedCharacterInSettings;
        set => this.RaiseAndSetIfChanged(ref _selectedCharacterInSettings, value);
    }

    // Settings list selection — propagates to SelectedCharacterInSettings.
    private CharacterListItem? _selectedCharacterListItem;
    public CharacterListItem? SelectedCharacterListItem
    {
        get => _selectedCharacterListItem;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedCharacterListItem, value);
            SelectedCharacterInSettings = value?.Character;
        }
    }

    private Corporation? _selectedCorp;
    public Corporation? SelectedCorp
    {
        get => _selectedCorp;
        set => this.RaiseAndSetIfChanged(ref _selectedCorp, value);
    }

    // Settings corp list selection — propagates to SelectedCorp.
    private CorpListItem? _selectedCorpListItem;
    public CorpListItem? SelectedCorpListItem
    {
        get => _selectedCorpListItem;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedCorpListItem, value);
            SelectedCorp = value?.Corp;
        }
    }

    private string _statusMessage = "Add a character to get started.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    private bool _isAuthBusy;
    public bool IsAuthBusy
    {
        get => _isAuthBusy;
        private set => this.RaiseAndSetIfChanged(ref _isAuthBusy, value);
    }

    private CancellationTokenSource? _authCts;

    // -----------------------------------------------------------------------
    // Scope dialog support
    // -----------------------------------------------------------------------

    private readonly IReadOnlyList<ScopeGroup> _charScopeGroups;
    private readonly IReadOnlyList<ScopeGroup> _corpScopeGroups;

    private IReadOnlyList<ScopeGroup> _dialogScopeGroups = [];
    public IReadOnlyList<ScopeGroup> DialogScopeGroups
    {
        get => _dialogScopeGroups;
        private set => this.RaiseAndSetIfChanged(ref _dialogScopeGroups, value);
    }

    public Interaction<string, bool> ScopeSelectionInteraction { get; } = new();
    public Interaction<string, bool> ConfirmReplaceInteraction { get; } = new();

    // -----------------------------------------------------------------------
    // Details panel data (Settings window)
    // -----------------------------------------------------------------------

    private IReadOnlyList<ScopeDisplayGroup> _selectedCharacterScopeGroups = [];
    public IReadOnlyList<ScopeDisplayGroup> SelectedCharacterScopeGroups
    {
        get => _selectedCharacterScopeGroups;
        private set => this.RaiseAndSetIfChanged(ref _selectedCharacterScopeGroups, value);
    }

    private string _selectedCharacterScopeSummary = "";
    public string SelectedCharacterScopeSummary
    {
        get => _selectedCharacterScopeSummary;
        private set => this.RaiseAndSetIfChanged(ref _selectedCharacterScopeSummary, value);
    }

    private IReadOnlyList<ScopeDisplayGroup> _selectedCorpScopeGroups = [];
    public IReadOnlyList<ScopeDisplayGroup> SelectedCorpScopeGroups
    {
        get => _selectedCorpScopeGroups;
        private set => this.RaiseAndSetIfChanged(ref _selectedCorpScopeGroups, value);
    }

    private string _selectedCorpScopeSummary = "";
    public string SelectedCorpScopeSummary
    {
        get => _selectedCorpScopeSummary;
        private set => this.RaiseAndSetIfChanged(ref _selectedCorpScopeSummary, value);
    }

    private bool _selectedCorpIsPersonal;
    public bool SelectedCorpIsPersonal
    {
        get => _selectedCorpIsPersonal;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedCorpIsPersonal, value);
            var corp = _selectedCorp;
            if (corp is null) return;
            corp.IsPersonal = value;
            _ = SaveCorpIsPersonalAsync(corp);
            RefreshCorpListItem(corp);
        }
    }

    private async Task SaveCorpIsPersonalAsync(Corporation corp)
    {
        try
        {
            _db.Entry(corp).Property(c => c.IsPersonal).IsModified = true;
            await _db.SaveChangesAsync();
        }
        catch { }
    }

    // -----------------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------------

    public ReactiveCommand<Unit, Unit> AddCharacterCommand       { get; }
    public ReactiveCommand<Unit, Unit> UpdateCharacterCommand    { get; }
    public ReactiveCommand<Unit, Unit> AddCorporationCommand     { get; }
    public ReactiveCommand<Unit, Unit> UpdateCorporationCommand  { get; }
    public ReactiveCommand<Unit, Unit> RefreshSkillsCommand      { get; }
    public ReactiveCommand<Unit, Unit> RemoveCharacterCommand    { get; }
    public ReactiveCommand<Unit, Unit> RemoveCorporationCommand  { get; }
    public ReactiveCommand<Unit, Unit> CancelAddCommand          { get; }
    public ReactiveCommand<Unit, Unit> SelectAllScopesCommand    { get; }
    public ReactiveCommand<Unit, Unit> ClearAllScopesCommand     { get; }

    // -----------------------------------------------------------------------

    public CharacterViewModel(EsiAuthService auth, EsiClient esi, AppDbContext db)
    {
        _auth = auth;
        _esi  = esi;
        _db   = db;

        _charScopeGroups = BuildScopeGroups(EsiAuthService.CharacterScopes, stripCorporation: false);
        _corpScopeGroups = BuildScopeGroups(EsiAuthService.CorporationScopes, stripCorporation: true);

        this.WhenAnyValue(x => x.SelectedCharacterInSettings)
            .Subscribe(ch =>
            {
                SelectedCharacterScopeGroups  = BuildScopeDisplay(_charScopeGroups, ch?.GrantedScopes);
                SelectedCharacterScopeSummary = ScopeSummary(SelectedCharacterScopeGroups);
            });

        this.WhenAnyValue(x => x.SelectedCorp)
            .Subscribe(corp =>
            {
                if (corp is null)
                {
                    SelectedCorpScopeGroups  = [];
                    SelectedCorpScopeSummary = "";
                    _selectedCorpIsPersonal  = false;
                    this.RaisePropertyChanged(nameof(SelectedCorpIsPersonal));
                    return;
                }
                // Corp stores its own token data — no auth-character lookup needed.
                SelectedCorpScopeGroups  = BuildScopeDisplay(_corpScopeGroups, corp.GrantedScopes);
                SelectedCorpScopeSummary = ScopeSummary(SelectedCorpScopeGroups);
                _selectedCorpIsPersonal  = corp.IsPersonal;
                this.RaisePropertyChanged(nameof(SelectedCorpIsPersonal));
            });

        var notAuthBusy   = this.WhenAnyValue(x => x.IsAuthBusy, busy => !busy);
        var notSkillBusy  = this.WhenAnyValue(x => x.IsBusy,     busy => !busy);
        var canRefresh    = this.WhenAnyValue(x => x.IsBusy, x => x.SelectedCharacter,
                                (busy, sel) => !busy && sel is not null);
        var canUpdateChar = this.WhenAnyValue(x => x.IsAuthBusy, x => x.SelectedCharacterInSettings,
                                (busy, sel) => !busy && sel is not null);
        var canUpdateCorp = this.WhenAnyValue(x => x.IsAuthBusy, x => x.SelectedCorp,
                                (busy, sel) => !busy && sel is not null);
        var isAuthBusy    = this.WhenAnyValue(x => x.IsAuthBusy);

        AddCharacterCommand      = ReactiveCommand.CreateFromTask(AddCharacterAsync,       notAuthBusy);
        UpdateCharacterCommand   = ReactiveCommand.CreateFromTask(UpdateCharacterAsync,    canUpdateChar);
        AddCorporationCommand    = ReactiveCommand.CreateFromTask(AddCorporationAsync,     notAuthBusy);
        UpdateCorporationCommand = ReactiveCommand.CreateFromTask(UpdateCorporationAsync,  canUpdateCorp);
        RefreshSkillsCommand     = ReactiveCommand.CreateFromTask(LoadSkillsAsync,         canRefresh);
        RemoveCharacterCommand   = ReactiveCommand.CreateFromTask(RemoveCharacterAsync,    canUpdateChar);
        RemoveCorporationCommand = ReactiveCommand.CreateFromTask(RemoveCorpAsync,         canUpdateCorp);
        CancelAddCommand         = ReactiveCommand.Create(() => _authCts?.Cancel(),        isAuthBusy);
        SelectAllScopesCommand   = ReactiveCommand.Create(() => SetAllScopes(true));
        ClearAllScopesCommand    = ReactiveCommand.Create(() => SetAllScopes(false));

        var loadSkillsCommand = ReactiveCommand.CreateFromTask(LoadSkillsAsync, notSkillBusy);

        foreach (var cmd in new ReactiveCommandBase<Unit, Unit>[]
            { AddCharacterCommand, UpdateCharacterCommand, AddCorporationCommand, UpdateCorporationCommand,
              RefreshSkillsCommand, loadSkillsCommand, RemoveCharacterCommand, RemoveCorporationCommand })
        {
            cmd.ThrownExceptions.Subscribe(ex => StatusMessage = $"Error: {ex.Message}");
        }

        this.WhenAnyValue(x => x.SelectedCharacter)
            .Where(c => c is not null)
            .Select(_ => Unit.Default)
            .InvokeCommand(loadSkillsCommand);

        _ = LoadFromDatabaseAsync();
    }

    // -----------------------------------------------------------------------
    // Startup
    // -----------------------------------------------------------------------

    private async Task LoadFromDatabaseAsync()
    {
        try
        {
            var characters   = await _db.Characters.ToListAsync();
            var corporations = await _db.Corporations.ToListAsync();

            foreach (var ch in characters)
            {
                _esi.RegisterCharacter(ch.Id, ch.RefreshToken);
                Characters.Add(ch);
            }
            foreach (var ch in characters)
                CharacterListItems.Add(MakeCharacterListItem(ch));

            foreach (var corp in corporations)
            {
                Corporations.Add(corp);
                CorpListItems.Add(MakeCorpListItem(corp));
            }

            if (Characters.Count > 0)
            {
                SelectedCharacter = Characters[0];
                StatusMessage = $"Loaded {Characters.Count} character(s), {Corporations.Count} corporation(s).";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Startup load error: {ex.Message}";
        }
    }

    // -----------------------------------------------------------------------
    // Add Character
    // -----------------------------------------------------------------------

    private async Task AddCharacterAsync()
    {
        ResetAllSelected(_charScopeGroups);
        DialogScopeGroups = _charScopeGroups;

        var proceed = await ScopeSelectionInteraction.Handle("character");
        if (!proceed) return;

        _authCts   = new CancellationTokenSource();
        IsAuthBusy = true;
        StatusMessage = "Opening browser for character login…";
        try
        {
            var scopes      = GetSelectedScopes();
            var tokens      = await _auth.LoginAsync(scopes, _authCts.Token);
            var characterId = JwtHelper.GetCharacterId(tokens.AccessToken);
            _esi.SetTokens(characterId, tokens);
            var character = await UpsertCharacterAsync(characterId, tokens.RefreshToken, scopes, tokens.ExpiresAt);
            SelectedCharacter = character;
            StatusMessage = $"Character '{character.Name}' added.";
        }
        catch (OperationCanceledException) { StatusMessage = "Login cancelled."; }
        finally { IsAuthBusy = false; _authCts?.Dispose(); _authCts = null; }
    }

    // -----------------------------------------------------------------------
    // Update Character
    // -----------------------------------------------------------------------

    private async Task UpdateCharacterAsync()
    {
        if (SelectedCharacterInSettings is null) return;
        var target = SelectedCharacterInSettings;

        // Pre-select the full current scope set so an update always picks up scopes added to the
        // app since this character was last authed (e.g. the corp-roles scope). The user can still
        // deselect any before proceeding.
        ResetAllSelected(_charScopeGroups);
        DialogScopeGroups = _charScopeGroups;

        var proceed = await ScopeSelectionInteraction.Handle("character");
        if (!proceed) return;

        _authCts   = new CancellationTokenSource();
        IsAuthBusy = true;
        StatusMessage = $"Opening browser to update '{target.Name}'…";
        try
        {
            var scopes      = GetSelectedScopes();
            var tokens      = await _auth.LoginAsync(scopes, _authCts.Token);
            var characterId = JwtHelper.GetCharacterId(tokens.AccessToken);

            if (characterId != target.Id)
            {
                IsAuthBusy = false;
                var newInfo = await _esi.GetCharacterPublicAsync(characterId);
                var newName = newInfo?.Name ?? characterId.ToString();
                var msg = $"You authenticated as '{newName}', but '{target.Name}' was selected for update.\n\n" +
                          $"Replace '{target.Name}' with '{newName}'?";
                var confirmed = await ConfirmReplaceInteraction.Handle(msg);
                if (!confirmed) { StatusMessage = "Update cancelled."; return; }
                IsAuthBusy = true;
                await RemoveCharacterEntityAsync(target);
            }

            _esi.SetTokens(characterId, tokens);
            var character = await UpsertCharacterAsync(characterId, tokens.RefreshToken, scopes, tokens.ExpiresAt);
            SelectedCharacter = character;
            StatusMessage = $"Character '{character.Name}' updated.";
        }
        catch (OperationCanceledException) { StatusMessage = "Update cancelled."; }
        finally { IsAuthBusy = false; _authCts?.Dispose(); _authCts = null; }
    }

    // -----------------------------------------------------------------------
    // Add Corporation
    // -----------------------------------------------------------------------

    private async Task AddCorporationAsync()
    {
        ResetAllSelected(_corpScopeGroups);
        DialogScopeGroups = _corpScopeGroups;

        var proceed = await ScopeSelectionInteraction.Handle("corporation");
        if (!proceed) return;

        await RunCorpAuthFlowAsync(targetCorp: null,
            statusMsg: "Opening browser — log in as a character with director/accountant roles…");
    }

    // -----------------------------------------------------------------------
    // Update Corporation
    // -----------------------------------------------------------------------

    private async Task UpdateCorporationAsync()
    {
        if (SelectedCorp is null) return;
        var target = SelectedCorp;

        // Pre-select the full current corp scope set so an update always picks up scopes added to
        // the app since this corp was last authed. The user can still deselect any before proceeding.
        ResetAllSelected(_corpScopeGroups);
        DialogScopeGroups = _corpScopeGroups;

        var proceed = await ScopeSelectionInteraction.Handle("corporation");
        if (!proceed) return;

        await RunCorpAuthFlowAsync(targetCorp: target,
            statusMsg: $"Opening browser to update '{target.Name}'…");
    }

    // Shared browser flow for both Add and Update corporation.
    // The auth character's Character entity is intentionally NOT touched here — we only
    // need their ESI public info to resolve the corp ID. All token data lives on Corporation.
    private async Task RunCorpAuthFlowAsync(Corporation? targetCorp, string statusMsg)
    {
        _authCts   = new CancellationTokenSource();
        IsAuthBusy = true;
        StatusMessage = statusMsg;
        try
        {
            var scopes      = GetSelectedScopes();
            var tokens      = await _auth.LoginAsync(scopes, _authCts.Token);
            var characterId = JwtHelper.GetCharacterId(tokens.AccessToken);
            // Register tokens with EsiClient for corp API calls — Character entity untouched.
            _esi.SetTokens(characterId, tokens);

            var charInfo = await _esi.GetCharacterPublicAsync(characterId)
                           ?? throw new InvalidOperationException("Could not fetch character info from ESI.");
            var esiCorp  = await _esi.GetCorporationPublicAsync(charInfo.CorporationId)
                           ?? throw new InvalidOperationException("Could not fetch corporation info from ESI.");

            if (targetCorp is not null && charInfo.CorporationId != targetCorp.Id)
            {
                IsAuthBusy = false;
                var msg = $"The authenticated character belongs to '{esiCorp.Name}', " +
                          $"but '{targetCorp.Name}' was selected for update.\n\n" +
                          $"Replace '{targetCorp.Name}' with '{esiCorp.Name}'?";
                var confirmed = await ConfirmReplaceInteraction.Handle(msg);
                if (!confirmed) { StatusMessage = "Update cancelled."; return; }
                IsAuthBusy = true;

                // DB first, then UI — matches the pattern in RemoveCorpAsync.
                _db.Corporations.Remove(targetCorp);
                await _db.SaveChangesAsync();
                if (SelectedCorpListItem?.Corp == targetCorp) SelectedCorpListItem = null;
                SelectedCorp = null;
                var oldItem = CorpListItems.FirstOrDefault(i => i.Corp == targetCorp);
                if (oldItem is not null) CorpListItems.Remove(oldItem);
                Corporations.Remove(targetCorp);
                targetCorp = null;
            }

            var isNew      = false;
            var corpEntity = await _db.Corporations.FindAsync(charInfo.CorporationId);
            if (corpEntity is null)
            {
                corpEntity = new Corporation { Id = charInfo.CorporationId };
                isNew = true;
            }

            corpEntity.Name                 = esiCorp.Name;
            corpEntity.Ticker               = esiCorp.Ticker;
            corpEntity.AuthCharacterId      = characterId;
            corpEntity.RefreshToken         = tokens.RefreshToken;
            corpEntity.GrantedScopes        = string.Join(' ', scopes);
            corpEntity.AccessTokenExpiresAt = tokens.ExpiresAt;
            corpEntity.LastUpdated          = DateTimeOffset.UtcNow;

            // Flag which corp endpoints this character has no role to poll, so the poller skips
            // them instead of eating 403 "required role" errors. If the character's roles aren't
            // known yet they'll be filled in by the role poll (which recomputes this) and any gaps
            // self-heal on the first 403.
            var authRoles = await _db.EsiRoles.Where(rr => rr.CharacterId == characterId)
                .Select(rr => rr.Role).ToListAsync();
            if (authRoles.Count > 0)
                corpEntity.DeniedEndpoints = EsiPollingService.ComputeDeniedCorpEndpoints(authRoles);

            if (isNew)
            {
                _db.Corporations.Add(corpEntity);
                Corporations.Add(corpEntity);
                CorpListItems.Add(MakeCorpListItem(corpEntity));
            }
            else
            {
                RefreshCorpListItem(corpEntity);
            }

            await _db.SaveChangesAsync();

            // Push the fresh token into EsiClient so the polling service picks it up immediately
            // without waiting for an app restart or token expiry.
            _esi.SetCorpTokens(corpEntity.Id, tokens);

            var verb = isNew ? "added" : "updated";
            StatusMessage = $"Corporation [{esiCorp.Ticker}] {esiCorp.Name} {verb} (auth via {charInfo.Name}).";
        }
        catch (OperationCanceledException) { StatusMessage = "Login cancelled."; }
        finally { IsAuthBusy = false; _authCts?.Dispose(); _authCts = null; }
    }

    // -----------------------------------------------------------------------
    // Skills
    // -----------------------------------------------------------------------

    private async Task LoadSkillsAsync()
    {
        if (SelectedCharacter is null) return;

        IsBusy = true;
        StatusMessage = $"Loading skills for {SelectedCharacter.Name}…";
        try
        {
            var charId = SelectedCharacter.Id;
            var skills = await _esi.GetSkillsAsync(charId);
            var queue  = await _esi.GetSkillQueueAsync(charId);

            SelectedCharacter.TotalSp       = skills?.TotalSp       ?? SelectedCharacter.TotalSp;
            SelectedCharacter.UnallocatedSp = skills?.UnallocatedSp ?? SelectedCharacter.UnallocatedSp;
            SelectedCharacter.LastUpdated   = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync();

            var names = await ResolveTypeNamesAsync(
                (queue ?? []).Select(q => q.SkillId).Distinct().ToList());

            SkillQueue.Clear();
            if (queue is not null)
                foreach (var item in queue.OrderBy(q => q.QueuePosition))
                    SkillQueue.Add(new SkillQueueItemVm(
                        item.QueuePosition,
                        names.GetValueOrDefault(item.SkillId, $"Unknown Skill ({item.SkillId})"),
                        item.FinishedLevel,
                        item.FinishDate));

            StatusMessage = "";
        }
        finally { IsBusy = false; }
    }

    private Task<Dictionary<int, string>> ResolveTypeNamesAsync(IReadOnlyList<int> typeIds)
    {
        if (typeIds.Count == 0) return Task.FromResult(new Dictionary<int, string>());

        return _db.SdeTypes
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name);
    }

    // -----------------------------------------------------------------------
    // Status list-item helpers
    // -----------------------------------------------------------------------

    private static readonly IBrush RedBrush    = new SolidColorBrush(Color.Parse("#e05050"));
    private static readonly IBrush GreenBrush  = new SolidColorBrush(Color.Parse("#50c050"));
    private static readonly IBrush OrangeBrush = new SolidColorBrush(Color.Parse("#e0a030"));

    private static CharacterListItem MakeCharacterListItem(Character ch)
    {
        // Access tokens expire in ~20 min by design and auto-refresh — checking
        // AccessTokenExpiresAt always fires for any auth older than 20 minutes.
        // Status reflects scope coverage only; token validity is visible in the details panel.
        if (ch.GrantedScopes.Length == 0)
            return new CharacterListItem(ch, "Not Authenticated", OrangeBrush);

        var grantedSet = new HashSet<string>(ch.GrantedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var missing    = EsiAuthService.CharacterScopes.Count(s => !grantedSet.Contains(s));

        return missing == 0
            ? new CharacterListItem(ch, "All Scopes Included", GreenBrush)
            : new CharacterListItem(ch, $"{missing} scopes not granted", OrangeBrush);
    }

    private static CorpListItem MakeCorpListItem(Corporation corp)
    {
        if (corp.GrantedScopes.Length == 0)
            return new CorpListItem(corp, "Not Authenticated", OrangeBrush);

        var grantedSet = new HashSet<string>(corp.GrantedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var missing    = EsiAuthService.CorporationScopes.Count(s => !grantedSet.Contains(s));

        return missing == 0
            ? new CorpListItem(corp, "All Scopes Included", GreenBrush)
            : new CorpListItem(corp, $"{missing} scopes not granted", OrangeBrush);
    }

    private void RefreshCharacterListItem(Character ch)
    {
        var idx = CharacterListItems.IndexOf(CharacterListItems.FirstOrDefault(i => i.Character == ch)!);
        if (idx >= 0) CharacterListItems[idx] = MakeCharacterListItem(ch);
    }

    private void RefreshCorpListItem(Corporation corp)
    {
        var idx = CorpListItems.IndexOf(CorpListItems.FirstOrDefault(i => i.Corp == corp)!);
        if (idx >= 0) CorpListItems[idx] = MakeCorpListItem(corp);
    }

    // -----------------------------------------------------------------------
    // Scope helpers
    // -----------------------------------------------------------------------

    private static IReadOnlyList<ScopeGroup> BuildScopeGroups(string[] scopes, bool stripCorporation)
    {
        return scopes
            .Select(s => new ScopeItem(s, stripCorporation))
            .GroupBy(s => GetScopeCategory(s.Scope))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var category = stripCorporation && g.Key == "Corporations" ? "Corp" : g.Key;
                return new ScopeGroup(category, g);
            })
            .ToList()
            .AsReadOnly();
    }

    private static string GetScopeCategory(string scope)
    {
        var prefix   = scope.Split('.')[0];
        var category = prefix.StartsWith("esi-") ? prefix[4..] : prefix;
        return char.ToUpper(category[0]) + category[1..];
    }

    private static void ResetAllSelected(IReadOnlyList<ScopeGroup> groups)
    {
        foreach (var item in groups.SelectMany(g => g.Items))
            item.IsSelected = true;
    }

    private string[] GetSelectedScopes()
        => DialogScopeGroups
               .SelectMany(g => g.Items)
               .Where(i => i.IsSelected)
               .Select(i => i.Scope)
               .ToArray();

    private void SetAllScopes(bool selected)
    {
        foreach (var item in DialogScopeGroups.SelectMany(g => g.Items))
            item.IsSelected = selected;
    }

    private static IReadOnlyList<ScopeDisplayGroup> BuildScopeDisplay(
        IReadOnlyList<ScopeGroup> sourceGroups, string? grantedScopesStr)
    {
        var granted = grantedScopesStr is null
            ? new HashSet<string>()
            : new HashSet<string>(grantedScopesStr.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return sourceGroups
            .Select(g => new ScopeDisplayGroup(g.Category,
                g.Items.Select(i => new ScopeDisplayItem(i.FriendlyName, granted.Contains(i.Scope))).ToList()))
            .ToList()
            .AsReadOnly();
    }

    private static string ScopeSummary(IReadOnlyList<ScopeDisplayGroup> groups)
    {
        var granted = groups.Sum(g => g.Items.Count(i => i.IsGranted));
        var total   = groups.Sum(g => g.Items.Count);
        return total == 0 ? "no scopes stored" : $"{granted} of {total} scopes granted";
    }

    // -----------------------------------------------------------------------
    // Remove Character / Corporation
    // -----------------------------------------------------------------------

    // Removes a character entity. Corporations that used this character for auth
    // keep their own stored token data and remain in the list — they stand alone.
    // UI collections are updated AFTER SaveChanges so that a DB failure leaves
    // UI and DB consistent (the row stays visible if the delete fails).
    private async Task RemoveCharacterEntityAsync(Character character)
    {
        _db.Characters.Remove(character);
        await _db.SaveChangesAsync();

        if (SelectedCharacterListItem?.Character == character) SelectedCharacterListItem = null;
        if (SelectedCharacterInSettings == character) SelectedCharacterInSettings = null;
        if (SelectedCharacter           == character) SelectedCharacter = Characters.FirstOrDefault(c => c != character);

        var charItem = CharacterListItems.FirstOrDefault(i => i.Character == character);
        if (charItem is not null) CharacterListItems.Remove(charItem);
        Characters.Remove(character);
    }

    private async Task RemoveCharacterAsync()
    {
        if (SelectedCharacterInSettings is null) return;
        var toRemove = SelectedCharacterInSettings;
        await RemoveCharacterEntityAsync(toRemove);
        StatusMessage = $"Character '{toRemove.Name}' removed.";
    }

    private async Task RemoveCorpAsync()
    {
        if (SelectedCorp is null) return;
        var toRemove = SelectedCorp;

        _db.Corporations.Remove(toRemove);
        await _db.SaveChangesAsync();

        if (SelectedCorpListItem?.Corp == toRemove) SelectedCorpListItem = null;
        SelectedCorp = null;

        var corpItem = CorpListItems.FirstOrDefault(i => i.Corp == toRemove);
        if (corpItem is not null) CorpListItems.Remove(corpItem);
        Corporations.Remove(toRemove);
        StatusMessage = $"Corporation '{toRemove.Name}' removed.";
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<Character> UpsertCharacterAsync(long characterId, string refreshToken, string[] grantedScopes, DateTimeOffset tokenExpiresAt)
    {
        var publicInfo = await _esi.GetCharacterPublicAsync(characterId);

        var isNew  = false;
        var entity = await _db.Characters.FindAsync(characterId);
        // If EF holds this entity in "Deleted" state (left over from a previously failed
        // SaveChanges), detach it so we treat this auth as a fresh insert.
        if (entity is not null && _db.Entry(entity).State == EntityState.Deleted)
        {
            _db.Entry(entity).State = EntityState.Detached;
            entity = null;
        }
        if (entity is null)
        {
            entity = new Character { Id = characterId };
            isNew  = true;
        }

        entity.Name                 = publicInfo?.Name           ?? entity.Name;
        entity.CorporationId        = publicInfo?.CorporationId  ?? entity.CorporationId;
        entity.AllianceId           = publicInfo?.AllianceId;
        entity.SecurityStatus       = publicInfo?.SecurityStatus ?? entity.SecurityStatus;
        entity.RefreshToken         = refreshToken;
        entity.GrantedScopes        = string.Join(' ', grantedScopes);
        entity.AccessTokenExpiresAt = tokenExpiresAt;
        entity.LastUpdated          = DateTimeOffset.UtcNow;

        if (isNew)
        {
            _db.Characters.Add(entity);
            Characters.Add(entity);
            CharacterListItems.Add(MakeCharacterListItem(entity));
        }
        else
        {
            RefreshCharacterListItem(entity);
        }

        await _db.SaveChangesAsync();
        return entity;
    }
}

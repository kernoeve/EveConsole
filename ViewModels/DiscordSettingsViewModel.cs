using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using EveConsole.Localization;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>A named Discord webhook as the settings list shows it: never with its token.</summary>
public sealed record DiscordWebhookRow(int Id, string Name, string Link);

/// <summary>
/// Settings for Discord: the named webhooks, and which of them each part of the app posts to.
///
/// <para>Webhooks only, by decision — no bot and no sign-in. Laid out like the Slack tab's webhook
/// section so the two read as the same idea, with a Test button Slack's list does not have: a
/// Discord link is pasted from somebody else's server, and the first post is the only way to know
/// it works.</para>
/// </summary>
public class DiscordSettingsViewModel : ReactiveObject
{
    private readonly DiscordService _discord;

    public DiscordSettingsViewModel(DiscordService discord)
    {
        _discord = discord;

        AddWebhookCommand    = ReactiveCommand.CreateFromTask(AddWebhookAsync);
        RemoveWebhookCommand = ReactiveCommand.CreateFromTask<int>(RemoveWebhookAsync);
        TestWebhookCommand   = ReactiveCommand.CreateFromTask<int>(TestWebhookAsync);

        // Choices always has its "none" entry, so a picker is never bound to an empty list.
        Choices.Add(NoneChoice);

        // ⚠️ After the commands exist, and not awaited. The constructor cannot block on the
        // database, and the pickers show "none" until it returns either way.
        _ = ReloadWebhooksAsync();
    }

    /// <summary>The named webhooks, as the management grid shows them.</summary>
    public ObservableCollection<DiscordWebhookRow> Webhooks { get; } = [];

    /// <summary>This section's own status line: added, removed, tested.</summary>
    private string _webhookStatus = "";
    public string WebhookStatus
    {
        get => _webhookStatus;
        private set => this.RaiseAndSetIfChanged(ref _webhookStatus, value);
    }

    private string _newWebhookName = "";
    public string NewWebhookName
    {
        get => _newWebhookName;
        set => this.RaiseAndSetIfChanged(ref _newWebhookName, value);
    }

    private string _newWebhookUrl = "";
    public string NewWebhookUrl
    {
        get => _newWebhookUrl;
        set => this.RaiseAndSetIfChanged(ref _newWebhookUrl, value);
    }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    public ReactiveCommand<Unit, Unit> AddWebhookCommand    { get; }
    public ReactiveCommand<int,  Unit> RemoveWebhookCommand { get; }
    public ReactiveCommand<int,  Unit> TestWebhookCommand   { get; }

    private async Task AddWebhookAsync()
    {
        var name = NewWebhookName.Trim();
        var url  = NewWebhookUrl.Trim();

        if (name.Length == 0 || url.Length == 0)
        {
            WebhookStatus = SettingsText.DiscordWebhookNeedsNameUrl;
            return;
        }

        // Said plainly, rather than accepted and left to fail at the first post. A Slack link is
        // the likely mistake with both tabs side by side, so it gets its own answer.
        if (!DiscordService.IsWebhookUrl(url))
        {
            WebhookStatus = url.Contains("hooks.slack.com", StringComparison.OrdinalIgnoreCase)
                ? SettingsText.DiscordWebhookIsSlack
                : SettingsText.DiscordWebhookNotDiscord;
            return;
        }

        await _discord.AddWebhookAsync(name, url);
        NewWebhookName = "";
        NewWebhookUrl  = "";
        await ReloadWebhooksAsync();
        WebhookStatus = string.Format(SettingsText.SlackWebhookAdded, name);
    }

    /// <summary>
    /// Removes a webhook, unless a part of the app still posts to it.
    ///
    /// <para>⚠️ Refused rather than cascaded, as on the Slack tab: clearing the areas silently would
    /// make a post button vanish with nothing saying why. Naming them lets it be done on purpose.</para>
    /// </summary>
    private async Task RemoveWebhookAsync(int id)
    {
        var hook = Webhooks.FirstOrDefault(w => w.Id == id);
        if (hook is null) return;

        var areas = new List<string>();
        if (_discord.WebhookId(DiscordService.AreaCorpTop10)   == id) areas.Add(SettingsText.SlackAreaCorpTop10);
        if (_discord.WebhookId(DiscordService.AreaCorpMonthly) == id) areas.Add(SettingsText.SlackAreaMonthlySummary);
        if (_discord.WebhookId(DiscordService.AreaSalePosting) == id) areas.Add(SettingsText.SalePosting);

        if (areas.Count > 0)
        {
            WebhookStatus = string.Format(SettingsText.SlackWebhookInUse, hook.Name, string.Join(", ", areas));
            return;
        }

        await _discord.RemoveWebhookAsync(id);
        await ReloadWebhooksAsync();
        WebhookStatus = string.Format(SettingsText.SlackWebhookRemoved, hook.Name);
    }

    private async Task TestWebhookAsync(int id)
    {
        var hook = Webhooks.FirstOrDefault(w => w.Id == id);
        if (hook is null) return;

        IsBusy        = true;
        WebhookStatus = string.Format(SettingsText.DiscordTesting, hook.Name);
        try
        {
            var res = await _discord.TestAsync(id);
            WebhookStatus = res.Ok
                ? string.Format(SettingsText.DiscordTestSent, hook.Name)
                : string.Format(SettingsText.DiscordTestFailed, hook.Name, res.Error);
        }
        finally { IsBusy = false; }
    }

    private async Task ReloadWebhooksAsync()
    {
        var rows = await _discord.WebhooksAsync();

        // The grid is only looked at, never selected in, so it can simply be replaced.
        Webhooks.Clear();
        foreach (var w in rows) Webhooks.Add(new DiscordWebhookRow(w.Id, w.Name, DiscordService.Redact(w.Url)));

        // Every entry here is a Discord webhook, so each is shown by its name alone.
        SyncChoices(rows.Select(w => new Choice<int>(w.Id, w.Name)).ToList());
    }

    // — Per area ————————————————————————————————————————————————————

    private static readonly Choice<int> NoneChoice = new(0, SettingsText.DiscordNone);

    /// <summary>"None", then every named webhook: what each area's picker offers.</summary>
    public ObservableCollection<Choice<int>> Choices { get; } = [];

    /// <summary>
    /// Brings the picker list up to date one entry at a time.
    ///
    /// <para>⚠️ Never cleared and refilled. Clearing makes every ComboBox bound to it push null
    /// into its selection and then show blank — the Slack tab met exactly that and needed a guard
    /// for it. Entries that stay are kept as they are, so a picker holding one never notices.</para>
    /// </summary>
    private void SyncChoices(List<Choice<int>> hooks)
    {
        var wanted = new List<Choice<int>> { NoneChoice };
        wanted.AddRange(hooks);

        for (var i = Choices.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Choices[i])) Choices.RemoveAt(i);

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Choices.Count && Equals(Choices[i], wanted[i])) continue;

            var at = Choices.IndexOf(wanted[i]);
            if (at >= 0) Choices.Move(at, i);
            else         Choices.Insert(i, wanted[i]);
        }

        this.RaisePropertyChanged(nameof(CorpTop10Hook));
        this.RaisePropertyChanged(nameof(CorpMonthlyHook));
        this.RaisePropertyChanged(nameof(SalePostingHook));
    }

    /// <summary>The entry an area is set to: its webhook, or "none" when it has none or the one it
    /// had is no longer listed.</summary>
    private Choice<int> Resolve(string area)
        => _discord.WebhookId(area) is int id
            ? Choices.FirstOrDefault(c => c.Value == id) ?? NoneChoice
            : NoneChoice;

    private void Set(string area, Choice<int>? value)
    {
        // A detaching ComboBox sets null; that is not a choice.
        if (value is null) return;
        _ = _discord.SetWebhookIdAsync(area, value.Value > 0 ? value.Value : null);
    }

    public Choice<int> CorpTop10Hook
    {
        get => Resolve(DiscordService.AreaCorpTop10);
        set { Set(DiscordService.AreaCorpTop10, value); this.RaisePropertyChanged(); }
    }

    public Choice<int> CorpMonthlyHook
    {
        get => Resolve(DiscordService.AreaCorpMonthly);
        set { Set(DiscordService.AreaCorpMonthly, value); this.RaisePropertyChanged(); }
    }

    public Choice<int> SalePostingHook
    {
        get => Resolve(DiscordService.AreaSalePosting);
        set { Set(DiscordService.AreaSalePosting, value); this.RaisePropertyChanged(); }
    }
}

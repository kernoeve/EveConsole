using System.Collections.ObjectModel;
using System.Reactive;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>A channel that had messages in the selected window.</summary>
public class ChatChannelRowVm(string name, int count)
{
    public string Name    { get; } = name;
    public int    Count   { get; } = count;
    public string Display { get; } = $"{name}  ({count:N0})";
}

public class ChatMessageRowVm(ChatMessage m)
{
    public string Time     { get; } = LogViewerDates.ToLocalDisplay(m.OccurredAt);
    public string Sender   { get; } = m.SenderName;
    public string Message  { get; } = m.Message;
    public string Listener { get; } = m.ListenerName ?? "";
    public bool   IsSystem { get; } = m.IsSystemMessage;
}

/// <summary>
/// Viewer over the ChatMessages table: pick a date window, pick a channel that had
/// activity in it, read that channel.
///
/// Only shows what the importer was allowed to store — channels the user never ticked
/// were never read, so they simply won't appear here.
/// </summary>
public class ChatLogViewerViewModel : ReactiveObject
{
    private const int RowLimit = 5000;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;
    private readonly MonitoringSettings              _settings;
    private bool _isLoadingChannels;
    private bool _isLoadingMessages;

    public ObservableCollection<ChatChannelRowVm> Channels { get; } = [];
    public ObservableCollection<ChatMessageRowVm> Rows     { get; } = [];

    public ChatLogViewerViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        AppErrorLogger                  errorLogger,
        MonitoringSettings              settings)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        _settings    = settings;

        // Default window is the last 90 days.
        _dateFrom = DateTime.Now.AddDays(-90).ToString("yyyy-MM-dd");

        RefreshCommand = ReactiveCommand.Create(() => { _ = LoadChannelsAsync(); });
        _ = LoadChannelsAsync();
    }

    private string _dateFrom;
    public string DateFrom { get => _dateFrom; set { this.RaiseAndSetIfChanged(ref _dateFrom, value); _ = LoadChannelsAsync(); } }

    private string _dateThru = "";
    public string DateThru { get => _dateThru; set { this.RaiseAndSetIfChanged(ref _dateThru, value); _ = LoadChannelsAsync(); } }

    private ChatChannelRowVm? _selectedChannel;
    public ChatChannelRowVm? SelectedChannel
    {
        get => _selectedChannel;
        set { this.RaiseAndSetIfChanged(ref _selectedChannel, value); _ = LoadMessagesAsync(); }
    }

    private string _search = "";
    public string Search { get => _search; set { this.RaiseAndSetIfChanged(ref _search, value); _ = LoadMessagesAsync(); } }

    private bool _hideSystem;
    /// <summary>System lines (MOTD, channel-change, server status) are noise when
    /// reading a conversation, but they're the only movement signal here — so hiding
    /// them is a toggle rather than a permanent filter.</summary>
    public bool HideSystemMessages
    {
        get => _hideSystem;
        set { this.RaiseAndSetIfChanged(ref _hideSystem, value); _ = LoadMessagesAsync(); }
    }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private string _channelStatus = "";
    public string ChannelStatus { get => _channelStatus; private set => this.RaiseAndSetIfChanged(ref _channelStatus, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    private async Task LoadChannelsAsync()
    {
        if (_isLoadingChannels) return;
        _isLoadingChannels = true;
        ChannelStatus = "Loading…";

        try
        {
            var fromIso = LogViewerDates.ToIso(_dateFrom);
            var thruIso = LogViewerDates.ToIso(_dateThru);

            await using var db = await _dbFactory.CreateDbContextAsync();

            var q = db.ChatMessages.AsNoTracking().AsQueryable();
            if (fromIso is not null) q = q.Where(m => string.Compare(m.OccurredAt, fromIso) >= 0);
            if (thruIso is not null) q = q.Where(m => string.Compare(m.OccurredAt, thruIso) < 0);

            var grouped = await q.GroupBy(m => m.ChannelName)
                                 .Select(g => new { Name = g.Key, Count = g.Count() })
                                 .OrderByDescending(x => x.Count)
                                 .ToListAsync();

            var previous = _selectedChannel?.Name;

            Channels.Clear();
            foreach (var g in grouped) Channels.Add(new ChatChannelRowVm(g.Name, g.Count));

            // An empty list almost always means the importer was never switched on, not
            // that the date range is wrong — so say which, rather than leaving the user
            // adjusting dates against a table that was never going to have rows.
            ChannelStatus = grouped.Count > 0
                ? $"{grouped.Count:N0} channel(s)"
                : EmptyReason();

            // Keep the current selection if it still has activity in the new window.
            var restore = previous is null ? null : Channels.FirstOrDefault(c => c.Name == previous);
            _selectedChannel = restore;
            this.RaisePropertyChanged(nameof(SelectedChannel));

            await LoadMessagesAsync();
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(ChatLogViewerViewModel), "LoadChannels", ex);
            ChannelStatus = "Error loading channels.";
        }
        finally { _isLoadingChannels = false; }
    }

    /// <summary>Why there is nothing to show. Chat import has two independent gates and
    /// both are off by default, so "empty" is the expected first-run state — this
    /// distinguishes that from an unhelpful date range.</summary>
    private string EmptyReason()
    {
        if (!_settings.ChatEnabled)
            return "Chat log import is turned off — enable it in Settings → Chat Logs.";

        if (_settings.ChatChannels.Count == 0)
            return "No channels selected — pick channels in Settings → Chat Logs.";

        return "Nothing imported yet for the selected channels. New messages are picked "
             + "up as they happen; use Import Past Chat in Settings for older ones.";
    }

    private async Task LoadMessagesAsync()
    {
        if (_isLoadingMessages) return;

        if (_selectedChannel is null)
        {
            Rows.Clear();
            StatusText = Channels.Count == 0 ? EmptyReason() : "Select a channel.";
            return;
        }

        _isLoadingMessages = true;
        StatusText = "Loading…";

        try
        {
            var fromIso = LogViewerDates.ToIso(_dateFrom);
            var thruIso = LogViewerDates.ToIso(_dateThru);
            var channel = _selectedChannel.Name;

            await using var db = await _dbFactory.CreateDbContextAsync();

            var q = db.ChatMessages.AsNoTracking().Where(m => m.ChannelName == channel);
            if (fromIso is not null) q = q.Where(m => string.Compare(m.OccurredAt, fromIso) >= 0);
            if (thruIso is not null) q = q.Where(m => string.Compare(m.OccurredAt, thruIso) < 0);
            if (_hideSystem)         q = q.Where(m => !m.IsSystemMessage);

            if (!string.IsNullOrWhiteSpace(_search))
            {
                var s = _search.Trim();
                q = q.Where(m => EF.Functions.Like(m.Message, $"%{s}%")
                              || EF.Functions.Like(m.SenderName, $"%{s}%"));
            }

            var list = await q.OrderByDescending(m => m.OccurredAt).Take(RowLimit).ToListAsync();

            Rows.Clear();
            foreach (var m in list) Rows.Add(new ChatMessageRowVm(m));

            StatusText = list.Count == 0
                ? "No messages in range."
                : list.Count >= RowLimit
                    ? $"{list.Count:N0} messages (capped — narrow the range)"
                    : $"{list.Count:N0} message(s)";
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(ChatLogViewerViewModel), "LoadMessages", ex);
            StatusText = "Error loading messages.";
        }
        finally { _isLoadingMessages = false; }
    }
}

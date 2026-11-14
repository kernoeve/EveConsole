using System.Diagnostics;
using System.Reactive;
using EveConsole.Localization;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Settings → Online Services: the optional sign-in. Nothing on eveconsole.com is available until
/// it has been done, and it covers every client attached to this database.
/// </summary>
public class OnlineServicesSettingsViewModel : ReactiveObject
{
    private readonly OnlineServices _online;

    // EVE's page sends nothing back if its tab is simply closed, so the wait is always bounded,
    // and can be abandoned.
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private CancellationTokenSource? _signInCts;

    public OnlineServicesSettingsViewModel(OnlineServices online)
    {
        _online = online;

        SignInCommand    = ReactiveCommand.CreateFromTask(SignInAsync);
        CancelCommand    = ReactiveCommand.Create(() => { _signInCts?.Cancel(); });
        SignOutCommand   = ReactiveCommand.CreateFromTask(SignOutAsync);
        OpenTermsCommand = ReactiveCommand.Create(() => OpenUrl(OnlineServices.TermsUrl));
    }

    public ReactiveCommand<Unit, Unit> SignInCommand    { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand    { get; }
    public ReactiveCommand<Unit, Unit> SignOutCommand   { get; }
    public ReactiveCommand<Unit, Unit> OpenTermsCommand { get; }

    private bool _isSignedIn;
    public bool IsSignedIn
    {
        get => _isSignedIn;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isSignedIn, value);
            this.RaisePropertyChanged(nameof(CanSignIn));
        }
    }

    private bool _isSigningIn;
    public bool IsSigningIn
    {
        get => _isSigningIn;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isSigningIn, value);
            this.RaisePropertyChanged(nameof(CanSignIn));
        }
    }

    public bool CanSignIn => !IsSignedIn && !IsSigningIn;

    private string _status = SettingsText.OnlineNotSignedIn;
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string _usage = "";
    public string Usage { get => _usage; private set => this.RaiseAndSetIfChanged(ref _usage, value); }

    /// <summary>Reads the account and its usage again: the window opening, and after a change. A
    /// sign-in from another client on this database turns up here.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var account = await _online.AccountAsync();
            IsSignedIn = account is not null;
            Usage = "";

            if (account is null)
            {
                if (!IsSigningIn) Status = SettingsText.OnlineNotSignedIn;
                return;
            }

            Status = string.Format(SettingsText.OnlineSignedInAs, account.CharacterName);

            var usage = await _online.UsageAsync();

            // The key may have been refused just now, which signs this database out.
            if (await _online.AccountAsync() is null)
            {
                IsSignedIn = false;
                Status = SettingsText.OnlineNotSignedIn;
                return;
            }

            Usage = usage is null ? "" : string.Format(SettingsText.OnlineUsageLine,
                usage.StoredBytes / 1_048_576.0, usage.MaxStoredBytes / 1_048_576.0,
                usage.UploadsToday, usage.MaxUploadsPerDay);
        }
        catch (Exception ex)
        {
            // Nobody awaits this when the window opens, so the failure has to say so itself.
            Status = string.Format(CommonText.ErrorWithMessage, ex.Message);
        }
    }

    private async Task SignInAsync()
    {
        using var cts = new CancellationTokenSource(SignInTimeout);
        _signInCts = cts;
        IsSigningIn = true;
        Status = SettingsText.OnlineWaitingForBrowser;
        try
        {
            var outcome = await _online.SignInAsync(cts.Token);
            if (outcome.Ok) await RefreshAsync();
            else Status = outcome.Message;
        }
        catch (OperationCanceledException)
        {
            Status = SettingsText.OnlineSignInCancelled;
        }
        finally
        {
            _signInCts = null;
            IsSigningIn = false;
        }
    }

    private async Task SignOutAsync()
    {
        await _online.SignOutAsync();
        await RefreshAsync();
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* nothing sensible to do if no browser is available */ }
    }
}

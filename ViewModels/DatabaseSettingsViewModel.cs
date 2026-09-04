using System.Collections.ObjectModel;
using System.Diagnostics;
using ReactiveUI;
using EveConsole.Data;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EveConsole.ViewModels;

public class DatabaseSettingsViewModel : ReactiveObject
{
    private readonly AppPreferencesService  _prefs;
    private readonly DatabaseBackupService  _backupSvc;

    // ── Database type ─────────────────────────────────────────────────────────

    public const string SqliteName   = "SQLite";
    public const string PostgresName = "PostgreSQL";

    public ObservableCollection<string> DbTypes { get; } = [SqliteName, PostgresName];

    private string _selectedDbType = DbEngine.IsPostgres ? PostgresName : SqliteName;
    public string SelectedDbType
    {
        get => _selectedDbType;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedDbType, value);
            this.RaisePropertyChanged(nameof(IsSqlite));
            this.RaisePropertyChanged(nameof(IsPostgres));
            this.RaisePropertyChanged(nameof(EngineChanged));
            TestResultText = "";
            CanOfferCopy   = false;
        }
    }

    public bool IsSqlite   => SelectedDbType == SqliteName;
    public bool IsPostgres => SelectedDbType == PostgresName;

    /// <summary>True when the selection no longer matches what the app actually opened, which is
    /// the only time a restart is worth offering.</summary>
    public bool EngineChanged =>
        IsPostgres != DbEngine.IsPostgres;

    // ⚠️ Whether the app is reading its settings from beside the executable, shown because the
    // alternative is a user editing the file in app data and wondering why nothing changes.
    public string ConfigSourceText =>
        AppConfig.UsingPortableConfig
            ? $"Settings are being read from {AppConfig.PortableConfigPath} (beside the program), "
              + "not from app data."
            : "Settings are stored in app data. Place a config.json beside the program to give "
              + "this installation its own.";

    // ── PostgreSQL connection ─────────────────────────────────────────────────

    public PostgresSettings Pg { get; } =
        PostgresSettings.FromConnectionString(AppConfig.GetPostgresConnection());

    public string PgHost
    {
        get => Pg.Host;
        set { Pg.Host = value; this.RaisePropertyChanged(); ResetTest(); }
    }
    public string PgPort
    {
        get => Pg.Port.ToString();
        set { if (int.TryParse(value, out var n) && n is > 0 and < 65536) Pg.Port = n;
              this.RaisePropertyChanged(); ResetTest(); }
    }
    public string PgDatabase
    {
        get => Pg.Database;
        set { Pg.Database = value; this.RaisePropertyChanged(); ResetTest(); }
    }
    public string PgUsername
    {
        get => Pg.Username;
        set { Pg.Username = value; this.RaisePropertyChanged(); ResetTest(); }
    }
    public string PgPassword
    {
        get => Pg.Password;
        set { Pg.Password = value; this.RaisePropertyChanged(); ResetTest(); }
    }

    private void ResetTest()
    {
        TestResultText = "";
        CanOfferCopy   = false;
        TestSucceeded  = false;
    }

    private string _testResultText = "";
    public string TestResultText
    {
        get => _testResultText;
        private set { this.RaiseAndSetIfChanged(ref _testResultText, value);
                      this.RaisePropertyChanged(nameof(HasTestResult)); }
    }
    public bool HasTestResult => TestResultText.Length > 0;

    private bool _testSucceeded;
    public bool TestSucceeded
    {
        get => _testSucceeded;
        private set => this.RaiseAndSetIfChanged(ref _testSucceeded, value);
    }

    private bool _canOfferCopy;
    /// <summary>
    /// Set when the server answered and there is a SQLite database here to copy from. Whether the
    /// destination is empty decides WHICH copy is offered, not whether one is offered at all
    /// — see <see cref="DestinationTables"/>.
    /// </summary>
    public bool CanOfferCopy
    {
        get => _canOfferCopy;
        private set { this.RaiseAndSetIfChanged(ref _canOfferCopy, value);
                      RaiseCopyButtons(); }
    }

    private long _destinationTables;
    /// <summary>
    /// How many tables the last successful test found in schema public.
    ///
    /// <para>⚠️ The copy appends; it cannot merge. So a destination that already holds
    /// tables has to be emptied first, and that is destructive in a way nothing else on this
    /// screen is. This number decides between offering the ordinary copy and offering the one
    /// that erases — and it is only ever set by a test, so the wording the user reads
    /// describes a state that was observed rather than one that is assumed.</para>
    /// </summary>
    public long DestinationTables
    {
        get => _destinationTables;
        private set { this.RaiseAndSetIfChanged(ref _destinationTables, value);
                      this.RaisePropertyChanged(nameof(DestinationIsEmpty));
                      this.RaisePropertyChanged(nameof(DestinationHasData));
                      this.RaisePropertyChanged(nameof(CopyOfferText));
                      RaiseCopyButtons(); }
    }

    public bool DestinationIsEmpty => _destinationTables == 0;
    public bool DestinationHasData => _destinationTables > 0;

    /// <summary>
    /// Which of the two copy buttons is on screen, if either.
    ///
    /// <para>⚠️ Both hide while a copy runs, rather than merely disabling. A greyed-out
    /// "Erase Server Data and Copy" sitting beside Cancel for the hour a large copy takes is an
    /// invitation to press the wrong one, and the two do very different things.</para>
    /// </summary>
    public bool ShowCopyButton  => CanOfferCopy && DestinationIsEmpty && !IsCopying;
    public bool ShowEraseButton => CanOfferCopy && DestinationHasData && !IsCopying;

    private void RaiseCopyButtons()
    {
        this.RaisePropertyChanged(nameof(ShowCopyButton));
        this.RaisePropertyChanged(nameof(ShowEraseButton));
    }

    /// <summary>The paragraph above the copy button, a different offer in each case.</summary>
    public string CopyOfferText => DestinationIsEmpty
        ? "The server database is empty. Everything in the SQLite database can be copied into it "
          + "now. The SQLite file is only read, never changed, so this can be repeated if "
          + "something goes wrong."
        : $"The server database already holds {DestinationTables:N0} table(s). They can be erased "
          + "and replaced with the contents of the SQLite database. The SQLite file is still only "
          + "read and is not changed — but everything currently on the server is destroyed.";

    private double _copyPercent;
    /// <summary>
    /// How far the copy has got, by row.
    ///
    /// <para>⚠️ It used to measure tables, on the reasoning that the total row count could
    /// not be had without counting every table first, "which on a large database costs as much as
    /// some of the copying does". That was simply wrong: counting all 199 tables, 68 million rows,
    /// takes 0.96 seconds — nothing against a copy that runs for an hour.</para>
    ///
    /// <para>The cost of being wrong about it was a bar that did not move. The tables are wildly
    /// uneven: KillMailItems alone is over half the database, so by tables the bar sat on one
    /// number for most of the copy while everything was working perfectly.</para>
    /// </summary>
    public double CopyPercent
    {
        get => _copyPercent;
        private set => this.RaiseAndSetIfChanged(ref _copyPercent, value);
    }

    private string _copyTableText = "";
    /// <summary>"12,481,003 of 68,070,892 rows", for the label beside the bar.</summary>
    public string CopyTableText
    {
        get => _copyTableText;
        private set => this.RaiseAndSetIfChanged(ref _copyTableText, value);
    }

    private string _copyStatusText = "";
    public string CopyStatusText
    {
        get => _copyStatusText;
        private set => this.RaiseAndSetIfChanged(ref _copyStatusText, value);
    }

    private bool _isCopying;
    public bool IsCopying
    {
        get => _isCopying;
        private set { this.RaiseAndSetIfChanged(ref _isCopying, value);
                      this.RaisePropertyChanged(nameof(CanTest));
                      RaiseCopyButtons(); }
    }

    public bool CanTest => !IsCopying;

    /// <summary>
    /// How the password is actually being held. ⚠️ Read from SecretStore rather than
    /// assumed, because "your password is protected" is a claim that must never be made when it
    /// is not: on a machine with no keyring the value really is in the file as typed.
    /// </summary>
    public string PasswordProtectionText => SecretStore.Description;

    // ── SQLite info ───────────────────────────────────────────────────────────

    public string DbPath { get; private set; } = AppConfig.GetDbPath();

    private string _dbFileSizeText = "";
    public string DbFileSizeText
    {
        get => _dbFileSizeText;
        private set => this.RaiseAndSetIfChanged(ref _dbFileSizeText, value);
    }

    // ── Backup settings ───────────────────────────────────────────────────────

    private bool _backupEnabled;
    public bool BackupEnabled
    {
        get => _backupEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _backupEnabled, value);
            _ = _prefs.SetAsync(DatabaseBackupService.KeyEnabled, value ? "true" : "false");
        }
    }

    public ObservableCollection<string> BackupIntervals { get; } = ["Hourly", "Daily", "Weekly", "Monthly"];

    private string _selectedInterval = "Daily";
    public string SelectedInterval
    {
        get => _selectedInterval;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedInterval, value);
            _ = _prefs.SetAsync(DatabaseBackupService.KeyInterval, value.ToLowerInvariant());
        }
    }

    private int _backupsToKeep = 7;
    public int BackupsToKeep
    {
        get => _backupsToKeep;
        set
        {
            this.RaiseAndSetIfChanged(ref _backupsToKeep, value);
            _ = _prefs.SetLongAsync(DatabaseBackupService.KeyKeepCount, value);
        }
    }

    // ── Last backup info ──────────────────────────────────────────────────────

    private string _lastBackupText = "Never";
    public string LastBackupText
    {
        get => _lastBackupText;
        private set => this.RaiseAndSetIfChanged(ref _lastBackupText, value);
    }

    // ── Status / busy ─────────────────────────────────────────────────────────

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    // ── Dialog delegates (wired in code-behind) ───────────────────────────────

    public Func<string, string, Task<string?>>?    ShowSaveFileDialog  { get; set; }
    public Func<string, Task<string?>>?            ShowOpenFileDialog  { get; set; }
    public Func<string, string, Task<bool>>?       ShowConfirmDialog   { get; set; }

    /// <summary>
    /// A confirmation that requires the user to type a phrase, for the destructive case.
    ///
    /// <para>Separate from ShowConfirmDialog rather than an extra parameter on it, so the five
    /// ordinary confirmations in this class keep their shape and it stays obvious which one is
    /// the dangerous action.</para>
    /// </summary>
    public Func<string, string, string, Task<bool>>? ShowTypedConfirmDialog { get; set; }
    public Action?                                 RequestRestart      { get; set; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public DatabaseSettingsViewModel(AppPreferencesService prefs, DatabaseBackupService backupSvc)
    {
        _prefs     = prefs;
        _backupSvc = backupSvc;

        // Load persisted settings
        _backupEnabled    = backupSvc.BackupEnabled;
        _selectedInterval = CapitalizeFirst(backupSvc.Interval);
        _backupsToKeep    = backupSvc.KeepCount;

        // Startup-time database work reports itself here rather than in the error log: it succeeded
        // or it did not, and either way the person who asked for it is the one who should see the
        // outcome. Relocation first, since a shrink after one is the less surprising of the two.
        if (DatabaseRelocationService.LastResult is { Ran: true } moved)
            StatusText = moved.Message;
        if (DatabaseShrinkService.LastResult is { Ran: true } shrink)
            StatusText = shrink.Message;

        RefreshDbInfo();
        RefreshLastBackupText();
    }

    // ── PostgreSQL: test, save, copy ──────────────────────────────────────────

    /// <summary>
    /// Opens the connection and reports what it found, in the terms that decide what happens
    /// next: can it connect, may this user create tables, and is the database empty.
    ///
    /// <para>⚠️ The privilege check matters as much as the connection. Since PostgreSQL 15 a
    /// non-owner gets no CREATE on the public schema by default, so an administrator can create
    /// a database, grant "all privileges" on it, and the app still cannot make a single table.
    /// Finding that out here, by name, is the difference between a clear message and a failure
    /// on first launch that looks like a bug in the app.</para>
    /// </summary>
    public async Task TestPostgresAsync()
    {
        TestResultText = "Connecting…";
        TestSucceeded  = false;
        CanOfferCopy   = false;

        if (string.IsNullOrWhiteSpace(Pg.Host) || string.IsNullOrWhiteSpace(Pg.Database)
            || string.IsNullOrWhiteSpace(Pg.Username))
        {
            TestResultText = "Host, database and username are all required.";
            return;
        }

        try
        {
            await using var conn = new NpgsqlConnection(Pg.ToConnectionString());
            await conn.OpenAsync();

            async Task<string> Scalar(string sql)
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                return (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
            }

            var version = await Scalar("SHOW server_version");

            // ⚠️ Existence before privilege. has_schema_privilege THROWS when the schema is not
            // there — 3F000, schema "public" does not exist — and that exception used to
            // land in the catch below and be reported as "Could not connect", which is both wrong
            // and unactionable: the connection was fine. Somebody who dropped the schema and did
            // not put it back needs one statement, so say which one.
            var hasPublic = await Scalar(
                "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'public')");

            if (!string.Equals(hasPublic, "True", StringComparison.OrdinalIgnoreCase))
            {
                TestResultText =
                    $"Connected to PostgreSQL {version}, but the database has no \"public\" schema, "
                    + "so there is nowhere to create tables. Run: CREATE SCHEMA public;";
                return;
            }

            var canCreate = await Scalar(
                "SELECT has_schema_privilege(current_user, 'public', 'CREATE')");
            var tables = long.TryParse(await Scalar(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"),
                out var t) ? t : 0;

            if (!string.Equals(canCreate, "True", StringComparison.OrdinalIgnoreCase))
            {
                TestResultText =
                    $"Connected to PostgreSQL {version}, but {Pg.Username} cannot create tables in "
                    + "schema public. The simplest fix is to make this user the database's owner: "
                    + $"ALTER DATABASE \"{Pg.Database}\" OWNER TO \"{Pg.Username}\";";
                return;
            }

            TestSucceeded     = true;
            DestinationTables = tables;

            // A copy needs somewhere to copy FROM, which means this app is still running on the
            // SQLite file it would read. Once it is running on the server there is no source.
            CanOfferCopy = DbEngine.IsSqlite && File.Exists(AppConfig.GetDbPath());

            if (tables == 0)
            {
                TestResultText = CanOfferCopy
                    ? $"Connected to PostgreSQL {version}. The database is empty, so the data "
                      + "already here can be copied into it."
                    : $"Connected to PostgreSQL {version}. The database is empty and will be "
                      + "built on the next start.";
            }
            else
            {
                TestResultText =
                    $"Connected to PostgreSQL {version}. The database already holds {tables:N0} "
                    + "table(s), so it will be used as it is — nothing is copied into it "
                    + "unless you erase it first.";
            }
        }
        catch (Exception ex)
        {
            TestResultText = $"Could not connect: {ex.Message}";
        }
    }

    /// <summary>Stores the engine and connection, to take effect on the next start.</summary>
    /// <summary>
    /// Records the engine and restarts into it.
    ///
    /// <para>The setting only takes effect on a start — the database is opened once, early,
    /// and nothing reopens it — so leaving the user to restart by hand meant an application
    /// that said it was on PostgreSQL while still reading SQLite. Doing it here removes the step
    /// where that is possible.</para>
    /// </summary>
    public async Task SaveDatabaseChoiceAsync()
    {
        var target = IsPostgres ? "PostgreSQL" : "SQLite";

        if (ShowConfirmDialog is not null)
        {
            var ok = await ShowConfirmDialog(
                "Save and restart",
                $"Use {target} from now on?\n\n"
                + "EVE Console restarts immediately, because the database is opened once at "
                + "startup and nothing reopens it.\n\n"
                + "This copies and deletes nothing. It changes only which database the "
                + "application opens, and can be changed back the same way.");
            if (!ok) return;
        }

        if (IsPostgres) AppConfig.SetDbBackend(DbBackend.Postgres, Pg.ToConnectionString());
        else            AppConfig.SetDbBackend(DbBackend.Sqlite);

        this.RaisePropertyChanged(nameof(EngineChanged));
        StatusText = $"Set to {target}. Restarting…";
        await Task.Delay(800);
        RequestRestart?.Invoke();
    }

    private CancellationTokenSource? _copyCts;

    /// <summary>
    /// Copies everything from the SQLite file into the empty server database.
    ///
    /// <para>⚠️ The engine is NOT switched by this. Copying and switching are separate acts on
    /// purpose: a copy that half-finished and then re-pointed the app would leave the user on a
    /// partial database with no obvious way back, whereas a failed copy against an untouched
    /// SQLite file costs only the time.</para>
    /// </summary>
    public async Task CopyToPostgresAsync()
    {
        if (IsCopying || !CanOfferCopy) return;

        var wipeFirst = DestinationHasData;

        if (wipeFirst)
        {
            // ⚠️ Typed, not clicked. Everything else on this screen is additive or
            // undoable; this destroys rows that exist nowhere else, and a dialog that needs only
            // a click is one stray Enter away from doing it. Fail closed when the dialog is not
            // wired: an absent confirmation must never be read as consent.
            if (ShowTypedConfirmDialog is null) return;

            var erase = await ShowTypedConfirmDialog(
                "Erase the server database and copy",
                $"This PERMANENTLY DESTROYS everything in the database \"{Pg.Database}\" on "
                + $"{Pg.Host}.\n\n"
                + $"All {DestinationTables:N0} table(s) in schema \"public\", and every row in "
                + "them, are dropped. This CANNOT be undone. Nothing but a backup taken "
                + "beforehand will bring the data back, and EVE Console does not take one for "
                + "you.\n\n"
                + $"The data here — {AppConfig.GetDbPath()} — is then copied in. That "
                + "SQLite file is only read and is not changed.\n\n"
                + "Be certain this is the right database before you continue.",
                "ERASE");
            if (!erase) return;
        }
        else if (ShowConfirmDialog is not null)
        {
            var ok = await ShowConfirmDialog(
                "Copy data to PostgreSQL",
                $"Copy everything from {AppConfig.GetDbPath()} into {Pg.Database} on {Pg.Host}?\n\n"
                + "The SQLite database is only read and is not changed. A large database takes a "
                + "while, and the app should not be used until it finishes.");
            if (!ok) return;
        }

        IsCopying     = true;
        _copyCts      = new CancellationTokenSource();
        CopyStatusText = "Preparing the destination…";

        CopyPercent   = 0;
        CopyTableText = "";

        _copiedSoFar  = 0;

        // ⚠️ Constructed HERE, on the UI thread, and deliberately not inside the Task.Run
        // below. Progress<T> captures the synchronization context of wherever it is created and
        // posts callbacks back to it; built inside the background task it captures the thread
        // pool instead, and every update then raises PropertyChanged off the UI thread. That is
        // exactly what made the Corp Activity type filter silently do nothing.
        var progress = new Progress<CopyProgress>(p =>
        {
            CopyPercent    = p.RowsExpected == 0
                           ? 0
                           : Math.Min(100.0, 100.0 * p.RowsTotal / p.RowsExpected);
            CopyTableText  = $"{p.RowsTotal:N0} of {p.RowsExpected:N0} rows";
            CopyStatusText = $"table {p.TableIndex:N0} of {p.TableCount:N0} — {p.Table}: "
                           + $"{p.RowsInTable:N0} row(s)";
            _copiedSoFar   = p.RowsTotal;
        });

        // The same device for plain status lines, so the steps before the first table can say
        // what they are doing. Declared as the interface because Progress<T>.Report is an
        // explicit implementation and is not reachable through the concrete type.
        IProgress<string> status = new Progress<string>(s => CopyStatusText = s);

        var sqlitePath = AppConfig.GetDbPath();
        // Through the same helper the app uses, so the copy talks to the server on exactly the
        // terms everything else does. It makes no difference to a binary COPY, which writes typed
        // values rather than text — but a discrepancy here is one more thing to reason about
        // the next time something is added to this path.
        var pgConn     = AppDb.PostgresConnectionString(Pg.ToConnectionString());

        try
        {
            var rows = await Task.Run(async () =>
            {
                // ⚠️ Opened READ-ONLY. Nothing in the copy writes to the source, but saying so
                // in the connection string makes it a rule SQLite enforces rather than a property
                // of this code that a later edit could quietly break. Somebody migrating has
                // every reason to expect the database they are leaving to be untouched, and to be
                // able to point back at it if the server does not suit them.
                AppDbContext OpenSqlite() => new(new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={sqlitePath};Mode=ReadOnly;Pooling=False").Options);
                AppDbContext OpenPg() => new(new DbContextOptionsBuilder<AppDbContext>()
                    .UseNpgsql(pgConn).Options);

                // ⚠️ Built through the app's own bootstrap, not by the copy. Anything else
                // would be a third definition of the schema, free to drift from the two that
                // already have to be kept in step.
                if (wipeFirst)
                {
                    status.Report("Erasing the destination database…");
                    var wiped = await PostgresWipeService.WipeAsync(pgConn, _copyCts.Token);
                    status.Report(
                        $"Erased {wiped.Tables:N0} table(s). Building the schema…");
                }

                using (var dst = OpenPg())
                {
                    await dst.Database.EnsureCreatedAsync(_copyCts.Token);
                    PostgresSchema.Apply(dst, includeSeeds: false);
                }

                return await DatabaseCopyService.CopyAsync(
                    OpenSqlite, OpenPg, pgConn, progress, _copyCts.Token);
            });

            CopyPercent    = 100;
            CopyTableText  = "";
            CopyStatusText = $"Copied {rows:N0} row(s). The SQLite database is unchanged. "
                           + "Save the setting and restart to use PostgreSQL.";
            CanOfferCopy   = false;
        }
        catch (OperationCanceledException)
        {
            // The partial copy is left where it is rather than tidied away: it is a state the
            // user can look at, and testing the connection again now offers to erase it.
            CopyStatusText = $"Copy cancelled after {_copiedSoFar:N0} row(s). The server database "
                           + "holds a partial copy — test the connection again to erase it "
                           + "and start over.";
        }
        catch (Exception ex)
        {
            CopyStatusText = $"Copy failed: {ex.Message}";
        }
        finally
        {
            IsCopying = false;
            _copyCts?.Dispose();
            _copyCts = null;
        }
    }

    private long _copiedSoFar;

    /// <summary>
    /// Asks before cancelling.
    ///
    /// <para>⚠️ This button sits beside a progress bar that can be running for the better
    /// part of an hour, one position away from the button that starts the copy, and pressing it
    /// throws the whole run away: a partial copy cannot be resumed, so starting again means
    /// erasing the destination and copying everything from the beginning. Far too much to lose to
    /// a misclick.</para>
    ///
    /// <para>The copy keeps running while the question is on screen, so answering "no" costs
    /// nothing — and a copy that finishes while the dialog is open is left alone.</para>
    /// </summary>
    public async Task CancelCopyAsync()
    {
        var cts = _copyCts;
        if (cts is null || !IsCopying) return;

        if (ShowConfirmDialog is not null)
        {
            var ok = await ShowConfirmDialog(
                "Stop the copy",
                $"Stop copying? {_copiedSoFar:N0} row(s) have been copied so far and none of it "
                + "is kept: a partial copy cannot be resumed, so starting again means erasing the "
                + "server database and copying everything from the beginning.\n\n"
                + "The copy is still running while this question is open.");
            if (!ok) return;
        }

        // ⚠️ Re-checked, because the copy may well have finished while the dialog was open.
        // The token source is disposed the moment it does, and cancelling a disposed one throws.
        if (!IsCopying || !ReferenceEquals(_copyCts, cts)) return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // ── Backups on a server ───────────────────────────────────────────────────

    private string _pgDumpStatusText = "";
    public string PgDumpStatusText
    {
        get => _pgDumpStatusText;
        private set { this.RaiseAndSetIfChanged(ref _pgDumpStatusText, value);
                      this.RaisePropertyChanged(nameof(HasPgDumpStatus)); }
    }
    public bool HasPgDumpStatus => PgDumpStatusText.Length > 0;

    /// <summary>
    /// Reports whether pg_dump can be used, so the answer is on screen before somebody presses
    /// Back Up Now and waits for a failure.
    ///
    /// <para>⚠️ Not called automatically on every visit to the tab: it starts a process to read
    /// a version number, and doing that unasked each time the settings window opens is a cost
    /// nobody agreed to.</para>
    /// </summary>
    /// <summary>
    /// Puts a dump back, replacing whatever the database holds now.
    ///
    /// <para>⚠️ The only destructive action in the app — everything else adds, updates or
    /// moves aside. So it names the file and the database it will overwrite, and asks for the
    /// database name to be typed rather than accepting a click. A restore is also the thing
    /// somebody reaches for while already upset about losing data, which is exactly when a
    /// mis-click costs the most.</para>
    ///
    /// <para>⚠️ It only RECORDS the request. The work happens on the next start, before
    /// anything opens the database, because pg_restore drops every object and the connection
    /// pool must not be holding them while it does.</para>
    /// </summary>
    public async Task RestoreFromDumpAsync()
    {
        if (ShowOpenFileDialog is null) return;

        var file = await ShowOpenFileDialog("Choose a PostgreSQL dump to restore");
        if (string.IsNullOrWhiteSpace(file)) return;

        if (ShowTypedConfirmDialog is not null)
        {
            var ok = await ShowTypedConfirmDialog(
                $"Replace {Pg.Database} with this backup?",
                $"Everything now in {Pg.Database} on {Pg.Host} will be dropped and replaced by "
                + $"{Path.GetFileName(file)}.\n\n"
                + "This cannot be undone, and anything polled since that backup was taken is "
                + "lost. EVE Console will restart to do it.",
                Pg.Database);
            if (!ok) return;
        }

        AppConfig.SetRestorePending(file);
        StatusText = $"Restore scheduled: {Path.GetFileName(file)}. Restarting…";
        RequestRestart?.Invoke();
    }

    /// <summary>What the last restore did, reported once the app is back.</summary>
    public string RestoreResultText =>
        PgRestoreService.LastResult is { Ran: true } r ? r.Message : "";

    public bool HasRestoreResult => RestoreResultText.Length > 0;

    public async Task CheckPgDumpAsync()
    {
        PgDumpStatusText = "Looking for pg_dump…";
        var cs = AppConfig.GetPostgresConnection();
        if (string.IsNullOrWhiteSpace(cs))
        {
            PgDumpStatusText = "No PostgreSQL connection is configured yet.";
            return;
        }

        try   { PgDumpStatusText = (await PgDumpService.ProbeAsync(cs)).Message; }
        catch (Exception ex) { PgDumpStatusText = $"Could not check pg_dump: {ex.Message}"; }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public async Task BackupNowAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Backing up…";
        try
        {
            var result = await _backupSvc.BackupNowAsync(DbPath);
            RefreshLastBackupText();
            StatusText = result is not null
                ? $"Backup saved: {Path.GetFileName(result)}"
                : DbEngine.IsPostgres
                    ? "Backup failed — no PostgreSQL connection is configured."
                    : "Backup failed — DB file not found.";
        }
        catch (Exception ex)
        {
            StatusText = $"Backup error: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Moves the database, which covers renaming it: a rename is a move to the same folder under a
    /// different name, so one operation and one button serve both. The file picker decides which
    /// it is by where the user points it.
    ///
    /// <para>⚠️ Records the request and restarts; the move happens at startup before anything opens
    /// the database. This used to call File.Copy (Move) and File.Move (Rename) here, against a
    /// database the app had open and was writing to, which silently lost everything still in the
    /// WAL. See DatabaseRelocationService.</para>
    /// </summary>
    public async Task RelocateDatabaseAsync()
    {
        if (ShowSaveFileDialog is null || ShowConfirmDialog is null) return;

        var newPath = await ShowSaveFileDialog("Move or Rename Database…", Path.GetFileName(DbPath));
        if (newPath is null) return;

        if (string.Equals(newPath, DbPath, StringComparison.OrdinalIgnoreCase)) return;

        var sameFolder = string.Equals(
            Path.GetDirectoryName(newPath), Path.GetDirectoryName(DbPath),
            StringComparison.OrdinalIgnoreCase);

        var confirmed = await ShowConfirmDialog(
            sameFolder ? "Rename Database" : "Move Database",
            string.Join("\n\n",
                $"{(sameFolder ? "Rename" : "Move")} the database to:\n{newPath}",
                "EVE Console will restart and do this before it finishes starting, because it has " +
                "to happen while nothing is using the database.",
                "Within the same drive this is immediate whatever the size. Moving to a different " +
                "drive copies the file first and removes the original once that has succeeded, " +
                "which may take a while on a large database.",
                "Nothing is left at the old location."));
        if (!confirmed) return;

        AppConfig.SetPendingRelocation(newPath);
        StatusText = sameFolder ? "Restarting to rename the database…"
                                : "Restarting to move the database…";
        await Task.Delay(800);
        RequestRestart?.Invoke();
    }

    public async Task PointToExistingDatabaseAsync()
    {
        if (ShowOpenFileDialog is null || ShowConfirmDialog is null) return;

        var newPath = await ShowOpenFileDialog("Select Existing Database…");
        if (newPath is null) return;

        if (string.Equals(newPath, DbPath, StringComparison.OrdinalIgnoreCase)) return;

        var confirmed = await ShowConfirmDialog(
            "Switch Database",
            $"Point EVE Console to the existing database at:\n{newPath}\n\nThe application will restart.");
        if (!confirmed) return;

        AppConfig.SetDbPath(newPath);
        StatusText = "Done. Restarting…";
        await Task.Delay(800);
        RequestRestart?.Invoke();
    }

    /// <summary>
    /// Records the request and restarts. The work itself happens on the way back up, before
    /// anything opens the database — see DatabaseShrinkService for why it cannot happen here.
    /// </summary>
    public async Task ShrinkDatabaseAsync()
    {
        if (ShowConfirmDialog is null || IsBusy) return;

        var sizeText = File.Exists(DbPath) ? FormatBytes(new FileInfo(DbPath).Length) : "unknown size";

        // ⚠️ One line per paragraph. The dialog wraps to its own width, so hard breaks inside a
        // paragraph wrap twice and come out ragged.
        var message = string.Join("\n\n",
            "Deleting data does not make the database file smaller. SQLite keeps the freed pages and reuses them later, so the file stays at its largest size. Shrinking rebuilds it and returns that space to your drive.",
            $"The database is currently {sizeText}.",
            "EVE Console will restart and shrink the database before it finishes starting, because the rebuild needs the database entirely to itself. This may take a while on a large database, and the app is unavailable until it finishes.",
            "A backup copy is taken first and removed once the shrink succeeds.");

        var confirmed = await ShowConfirmDialog("Shrink Database", message);
        if (!confirmed) return;

        AppConfig.SetShrinkPending(true);
        StatusText = "Restarting to shrink the database…";
        await Task.Delay(800);
        RequestRestart?.Invoke();
    }


    // ── Storage breakdown ─────────────────────────────────────────────────────
    //
    // On demand rather than on open: it scans, and most visits to this tab are about backups.

    private readonly DatabaseSizeService _sizeSvc = new();

    public ObservableCollection<TableSizeVm> TableSizes { get; } = [];

    private bool _isAnalysing;
    public bool IsAnalysing
    {
        get => _isAnalysing;
        private set { this.RaiseAndSetIfChanged(ref _isAnalysing, value);
                      this.RaisePropertyChanged(nameof(CanAnalyse)); }
    }
    public bool CanAnalyse => !IsAnalysing;

    private string _sizeStatusText = "Not measured yet.";
    public string SizeStatusText
    {
        get => _sizeStatusText;
        private set => this.RaiseAndSetIfChanged(ref _sizeStatusText, value);
    }

    private string _sizeSummaryText = "";
    public string SizeSummaryText
    {
        get => _sizeSummaryText;
        private set => this.RaiseAndSetIfChanged(ref _sizeSummaryText, value);
    }

    public bool HasTableSizes => TableSizes.Count > 0;

    public async Task AnalyseSizesAsync()
    {
        if (IsAnalysing) return;
        IsAnalysing = true;

        // Cleared up front so a reload visibly replaces the previous result rather than appearing
        // to sit unchanged while the scan runs.
        TableSizes.Clear();
        this.RaisePropertyChanged(nameof(HasTableSizes));
        SizeSummaryText = "";

        try
        {
            var progress = new Progress<string>(s => SizeStatusText = s);

            // ⚠️ Not the same measurement on both engines, and the summary below says so.
            // SQLite has to estimate the sizes and can count the rows; PostgreSQL knows the sizes
            // exactly and estimates the rows. Presenting either as simply "the numbers" would
            // misrepresent one of them.
            var report = DbEngine.IsPostgres
                ? await new PostgresSizeService().AnalyseAsync(
                      AppConfig.GetPostgresConnection() ?? "", progress)
                : await _sizeSvc.AnalyseAsync(DbPath, progress);

            // Every table, including the empty ones: a table absent from the list reads as an
            // oversight, and the small ones are what make the big ones legible by comparison.
            // The service already returns them largest first.
            foreach (var t in report.Tables)
                TableSizes.Add(new TableSizeVm(t, report.UsedBytes));

            // ⚠️ Free-space-inside-the-file is deliberately NOT shown. FreeBytes is exact, but it
            // counts only wholly empty pages: deleting rows mostly leaves holes inside pages that
            // are still in use, so the figure reads as "reclaimable space" while being a floor far
            // below it. Only a VACUUM can answer the question people would ask of it.
            SizeSummaryText = DbEngine.IsPostgres
                ? $"Database {FormatBytes(report.FileBytes)} across {report.Tables.Count:N0} tables. "
                  + "Sizes are exact, including index and TOAST storage. Row counts are the "
                  + "server's own estimates, so a table written heavily since its last analyze "
                  + "reads low."
                : $"File {FormatBytes(report.FileBytes)} across {report.Tables.Count:N0} tables. "
                  + "Shares are of the space in use. Figures are measured and scaled to the file "
                  + "— good for comparing tables, not exact byte counts.";
            SizeStatusText = $"Loaded {DateTime.Now:HH:mm:ss}.";

            this.RaisePropertyChanged(nameof(HasTableSizes));
        }
        catch (Exception ex)
        {
            SizeStatusText = $"Could not measure: {ex.Message}";
        }
        finally { IsAnalysing = false; }
    }
    // ── Helpers ───────────────────────────────────────────────────────────────

    public void RefreshDbInfo()
    {
        DbPath = AppConfig.GetDbPath();
        this.RaisePropertyChanged(nameof(DbPath));
        try
        {
            if (File.Exists(DbPath))
            {
                var bytes = new FileInfo(DbPath).Length;
                DbFileSizeText = FormatBytes(bytes);
            }
            else
            {
                DbFileSizeText = "File not found";
            }
        }
        catch
        {
            DbFileSizeText = "Unknown";
        }
    }

    private void RefreshLastBackupText()
    {
        var last = _backupSvc.LastBackupUtc;
        if (last is null)
        {
            LastBackupText = "Never";
            return;
        }
        var local    = last.Value.ToLocalTime();
        var sizeText = _backupSvc.LastSizeBytes > 0 ? $" ({FormatBytes(_backupSvc.LastSizeBytes)})" : "";
        LastBackupText = $"{local:yyyy-MM-dd HH:mm:ss}{sizeText}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024)         return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes} B";
    }

    private static string CapitalizeFirst(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public void Dispose() { }
}

/// <summary>One row of the storage breakdown.</summary>
public sealed class TableSizeVm(TableSizeRow row, long usedBytes)
{
    public string Name      { get; } = row.Name;
    public string RowsText  { get; } = row.Rows.ToString("N0");
    public string TableText { get; } = Format(row.TableBytes);
    public string IndexText { get; } = row.IndexCount == 0 ? "—" : Format(row.IndexBytes);
    public string TotalText { get; } = Format(row.TableTotalBytes);

    public double SharePercent { get; } =
        usedBytes == 0 ? 0 : row.TableTotalBytes * 100.0 / usedBytes;
    public string ShareText { get; } =
        usedBytes == 0 ? "" : $"{row.TableTotalBytes * 100.0 / usedBytes:N1}%";

    /// <summary>Says outright when a figure came from a sample, so nobody reads four significant
    /// figures into a number that was extrapolated from 20,000 rows.</summary>
    public string Method { get; } = row.Estimated ? "sampled" : "counted";

    public string Tooltip { get; } = row.IndexCount == 0
        ? $"{row.Rows:N0} row(s), no indexes."
        : $"{row.Rows:N0} row(s), {row.IndexCount} index(es) costing {Format(row.IndexBytes)}.";

    private static string Format(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F2} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024)         return $"{bytes / 1_024.0:F0} KB";
        return $"{bytes} B";
    }
}

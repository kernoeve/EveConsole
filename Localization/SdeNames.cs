using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;

namespace EveConsole.Localization;

/// <summary>
/// SDE names — items, groups, places, NPC corporations and the rest — in the interface language,
/// for showing on a screen.
///
/// <para>⚠️ DISPLAY ONLY. The Name and DisplayName columns stay English, and everything that
/// matches on a name goes on reading them: the agent, parsers, saved alarm configs, search,
/// outgoing mail and Slack. Translate at the last step, where a name becomes text on a screen, and
/// never compare, store, send or search on what comes back from here.</para>
///
/// <para>The language is chosen when READING. The import stores all seven languages the SDE
/// carries besides English (<see cref="SdeName"/>), because several clients can share one
/// PostgreSQL database and each can run a different interface language. This loads the active
/// language's rows once, the first time a name is asked for, on a background thread.</para>
///
/// <para>Safe from any thread; never blocks, never throws. In English it does nothing at all and
/// never touches the database. In another language it returns the English it was handed until the
/// load has finished, and for any name the SDE gives only in English. Code that would rather wait
/// a moment than show English first awaits <see cref="EnsureLoadedAsync"/> before building its
/// rows:</para>
/// <code>
/// await SdeNames.EnsureLoadedAsync(ct);          // returns at once in English
/// Name = SdeNames.Type(type.TypeId, type.Name),  // the English column is the fallback
/// </code>
/// </summary>
public static class SdeNames
{
    /// <summary>
    /// The SDE's keys for the game client's languages other than English, in the order the import
    /// stores them. The interface's zh-Hans is the SDE's zh.
    /// </summary>
    public static IReadOnlyList<string> OtherLanguages { get; } = ["de", "es", "fr", "ja", "ko", "ru", "zh"];

    /// <summary>
    /// What an SDE import publishes when it commits, so that every client of a shared database
    /// reads the new names — see <see cref="ClientSignals"/>. Received by <see cref="TryApplySignal"/>.
    /// </summary>
    public const string ImportedSignal = """{"Kind":"sde-names"}""";
    private const string SignalKind = "sde-names";

    /// <summary>
    /// A load that failed, in a sentence for the error log. A delegate because this class knows
    /// nothing about logging; wired in <c>App.axaml.cs</c>, beside <see cref="AppConfig.WriteRefused"/>.
    ///
    /// <para>⚠️ Said, not swallowed: names quietly staying English is the kind of fault that gets
    /// diagnosed twice. The same failure again is not repeated.</para>
    /// </summary>
    public static Action<string>? LoadFailed { get; set; }

    /// <summary>
    /// Raised each time the interface language's names finish loading — the first time, and again
    /// after every SDE import. On a background thread: a listener marshals to the UI thread itself.
    /// A screen built before the first load shows English until it is built again; one that must
    /// not can listen here, or await <see cref="EnsureLoadedAsync"/> before building.
    /// </summary>
    public static event Action? Changed;

    // ── Lookups ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The name of <paramref name="id"/> in the interface language, or <paramref name="english"/>:
    /// in English, until the load has finished, and for a name the SDE does not translate.
    /// </summary>
    /// <param name="english">The entity's own English column — Name, or DisplayName for the kinds
    /// that name it (<see cref="SdeNameKind"/>).</param>
    public static string Get(SdeNameKind kind, long id, string english)
    {
        var lang = Language;
        if (lang is null) return english;

        var loaded = Loaded(lang);
        return loaded is not null && loaded.For(kind).TryGetValue(id, out var name) ? name : english;
    }

    // The kinds screens show most. Every other kind goes through Get.
    public static string Type(long typeId, string english)                  => Get(SdeNameKind.Type,           typeId,          english);
    public static string Group(long groupId, string english)                => Get(SdeNameKind.Group,          groupId,         english);
    public static string Category(long categoryId, string english)          => Get(SdeNameKind.Category,       categoryId,      english);
    public static string MarketGroup(long marketGroupId, string english)    => Get(SdeNameKind.MarketGroup,    marketGroupId,   english);
    public static string MetaGroup(long metaGroupId, string english)        => Get(SdeNameKind.MetaGroup,      metaGroupId,     english);
    public static string Region(long regionId, string english)              => Get(SdeNameKind.Region,         regionId,        english);
    public static string Constellation(long constellationId, string english) => Get(SdeNameKind.Constellation, constellationId, english);
    public static string SolarSystem(long solarSystemId, string english)    => Get(SdeNameKind.SolarSystem,    solarSystemId,   english);
    public static string Faction(long factionId, string english)            => Get(SdeNameKind.Faction,        factionId,       english);
    public static string NpcCorporation(long corporationId, string english) => Get(SdeNameKind.NpcCorporation, corporationId,   english);
    public static string Agent(long agentId, string english)                => Get(SdeNameKind.Agent,          agentId,         english);

    /// <summary>
    /// An NPC station's name: pass SdeStations.Name, which is ESI's English. ⚠️ Not the SDE's words
    /// but the import's — nothing ships a station's name in another language, so the import builds
    /// each from the station's parts the way the game client does (<see cref="LocationNames"/>).
    /// Safe with any location id: a player structure has no row and comes back as it is.
    /// </summary>
    public static string Station(long stationId, string english) => Get(SdeNameKind.Station, stationId, english);

    /// <summary>
    /// A location by its EVE id, for a column that can hold several kinds: a region, constellation,
    /// solar system or NPC station, told apart by the id's range. Anything else — a player
    /// structure, a planet or moon, a character, an asset container — comes back as it is.
    /// </summary>
    public static string Location(long id, string english) => id switch
    {
        >= 10_000_000 and <= 10_999_999 => Region(id, english),
        >= 20_000_000 and <= 20_999_999 => Constellation(id, english),
        >= 30_000_000 and <= 32_999_999 => SolarSystem(id, english),
        >= 60_000_000 and <= 63_999_999 => Station(id, english),
        _                               => english,
    };

    /// <summary>An attribute's DISPLAY name: pass SdeDogmaAttributes.DisplayName, not Name.</summary>
    public static string DogmaAttribute(long attributeId, string englishDisplayName) =>
        Get(SdeNameKind.DogmaAttribute, attributeId, englishDisplayName);

    /// <summary>A unit's DISPLAY name: pass SdeDogmaUnits.DisplayName, not Name.</summary>
    public static string DogmaUnit(long unitId, string englishDisplayName) =>
        Get(SdeNameKind.DogmaUnit, unitId, englishDisplayName);

    /// <summary>
    /// A name ESI resolved — /universe/names/ and the UniverseNames cache — by its ESI category,
    /// in the interface language where the SDE has it: item types, solar systems, constellations,
    /// regions, factions and NPC stations, and the NPC corporations and agents among corporations
    /// and characters. Everything else comes back as it is: players, their corporations and
    /// alliances, and structures.
    /// </summary>
    public static string ForEsiCategory(string? category, long id, string english) => category switch
    {
        "inventory_type" => Type(id, english),
        "solar_system"   => SolarSystem(id, english),
        "constellation"  => Constellation(id, english),
        "region"         => Region(id, english),
        "faction"        => Faction(id, english),
        "station"        => Station(id, english),
        "corporation"    => NpcCorporation(id, english),   // a player corporation has no row
        "character"      => Agent(id, english),            // nor has a player
        _                => english,
    };

    /// <summary>
    /// Every name of one kind in the interface language, by id — for code that builds its own
    /// id → name map. It holds only the names that differ from the English: look each id up, and
    /// fall back to the English column for any it lacks. Empty in English and until the load has
    /// finished. Never changed after it is handed out; a reload replaces it.
    /// </summary>
    public static IReadOnlyDictionary<long, string> Map(SdeNameKind kind)
    {
        var lang = Language;
        return lang is null ? Empty : Loaded(lang)?.For(kind) ?? Empty;
    }

    // ── Searching ───────────────────────────────────────────────────────────────
    //
    // A screen's own search box finds what the screen shows AND the English: the capsuleer reads
    // the game in their language, but pastes names from English sites, killboards and chat.

    /// <summary>
    /// Whether <paramref name="text"/> is in this entity's English or in the name the screen shows
    /// for it, ignoring case — for a filter that runs over rows already in memory.
    /// </summary>
    public static bool Matches(SdeNameKind kind, long id, string english, string text)
    {
        if (english.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        var shown = Get(kind, id, english);
        return !ReferenceEquals(shown, english) && shown.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The ids of one kind whose name in the interface language contains <paramref name="text"/>,
    /// ignoring case — for a search that runs in SQL on the English column, to add the ids of what
    /// was typed in the interface language (<c>… || ids.Contains(x.TypeId)</c>). Empty in English,
    /// and for blank text.
    /// </summary>
    public static IReadOnlyList<long> Find(SdeNameKind kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<long>();
        foreach (var (id, name) in Map(kind))
            if (name.Contains(text, StringComparison.OrdinalIgnoreCase)) found.Add(id);
        return found;
    }

    /// <summary>
    /// The ids of one kind whose name in the interface language IS <paramref name="name"/>,
    /// ignoring case in any script — for a box that takes a name typed as the screen shows it and
    /// stores the English. Empty in English, and for blank text.
    /// </summary>
    public static IReadOnlyList<long> Named(SdeNameKind kind, string name)
    {
        var text = name.Trim();
        if (text.Length == 0) return [];
        var found = new List<long>();
        foreach (var (id, shown) in Map(kind))
            if (string.Equals(shown, text, StringComparison.OrdinalIgnoreCase)) found.Add(id);
        return found;
    }

    /// <summary>
    /// Completes once the interface language's names are in: at once in English, or when they
    /// already are. For code that builds a screen's rows and would rather wait a moment, once,
    /// than show English first. Waits at most ten seconds, and not at all behind a load that has
    /// failed — the screen then shows English, and <see cref="Changed"/> says when names arrive.
    /// </summary>
    public static async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        var lang = Language;
        if (lang is null) return;
        if (Loaded(lang) is { } loaded && loaded.Generation == Volatile.Read(ref _generation)) return;

        Task? loading;
        lock (Gate) loading = _loading;
        if (loading is null) return;

        try { await loading.WaitAsync(MaxWait, ct).ConfigureAwait(false); }
        catch (TimeoutException) { /* English for now */ }
    }

    // ── Language ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The SDE's key for the interface language — de, es, fr, ja, ko, ru or zh. Null in English,
    /// and in any language the SDE does not carry: both read the English columns as they are.
    /// </summary>
    public static string? Language
    {
        get
        {
            // The language is settled once per run (Languages.ApplyAtStartup), so this is worked out
            // once rather than on every name a grid draws.
            var active = Languages.Active;
            var known  = _language;
            if (known is not null && ReferenceEquals(known.Ui, active)) return known.Code;

            var code = CodeFor(active.Culture);
            _language = new LanguageKey(active, code);
            return code;
        }
    }

    /// <summary>The SDE's key for a culture: its two-letter language, when the SDE has that language.</summary>
    public static string? CodeFor(CultureInfo culture)
    {
        var two = culture.TwoLetterISOLanguageName;
        return OtherLanguages.Contains(two) ? two : null;
    }

    // ── Refreshing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the names again, after an SDE import has replaced them. What is loaded goes on
    /// answering until the new names are in, so a screen never drops back to English in between.
    /// A language nobody has asked for yet stays unloaded until somebody does. The descriptions
    /// kept by <see cref="SdeTexts"/> are let go too: the import replaced those as well, and this
    /// is the one call — and <see cref="ImportedSignal"/> the one signal — that says so.
    /// </summary>
    public static void Reload()
    {
        lock (Gate)
        {
            _generation++;
            _retryAfterUtc = default;
        }

        SdeTexts.Reload();
        if (Language is { } lang && _snapshot is not null) StartLoad(lang);
    }

    /// <summary>
    /// Reloads on <see cref="ImportedSignal"/>, which another client's SDE import sends when it
    /// commits. False for any other signal, which is left to the handlers after this one.
    /// </summary>
    public static bool TryApplySignal(string payload)
    {
        // Every signal on the channel passes through here, and nearly all are something else.
        if (!payload.Contains(SignalKind, StringComparison.Ordinal)) return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("Kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || kind.GetString() != SignalKind)
                return false;
        }
        catch (JsonException) { return false; }

        Reload();
        return true;
    }

    // ── Loading ─────────────────────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<long, string> Empty = new Dictionary<long, string>();

    /// <summary>The highest kind this build knows; a row of a kind a newer build added is skipped.</summary>
    private static readonly int MaxKind = Enum.GetValues<SdeNameKind>().Max(k => (int)k);

    /// <summary>How long after a failed load the next one may be tried.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>The longest <see cref="EnsureLoadedAsync"/> holds a screen back.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(10);

    private static readonly object Gate = new();

    // Replaced whole and never changed after, so a reader needs no lock.
    private static volatile Snapshot?    _snapshot;
    private static volatile LanguageKey? _language;

    // Under Gate. _generation is read without it only through Volatile.Read.
    private static Task?    _loading;
    private static int      _generation;
    private static DateTime _retryAfterUtc;
    private static string?  _lastFailure;

    /// <summary>How the names are reached: the configured database, through AppDb. A field rather
    /// than a call so that a harness can point it at a database of its own.</summary>
    private static Func<DbConnection?> _connect = DefaultConnect;

    private sealed record LanguageKey(UiLanguage Ui, string? Code);

    /// <summary>One language's names, by kind, as one load read them.</summary>
    private sealed class Snapshot(string lang, int generation, Dictionary<long, string>[] byKind)
    {
        public string Lang       { get; } = lang;
        public int    Generation { get; } = generation;

        public IReadOnlyDictionary<long, string> For(SdeNameKind kind) =>
            (int)kind > 0 && (int)kind < byKind.Length ? byKind[(int)kind] : Empty;
    }

    /// <summary>
    /// What this language's names are answered from, or null before the first load is in. Starts
    /// a load when there is none, or when an import has made the loaded names stale.
    /// </summary>
    private static Snapshot? Loaded(string lang)
    {
        var snapshot = _snapshot;
        if (snapshot is null || snapshot.Lang != lang || snapshot.Generation != Volatile.Read(ref _generation))
            StartLoad(lang);
        return snapshot is not null && snapshot.Lang == lang ? snapshot : null;
    }

    private static void StartLoad(string lang)
    {
        lock (Gate)
        {
            // One at a time. A load already running looks again when it finishes, so an import
            // that lands while it runs is not missed.
            if (_loading is not null) return;
            if (DateTime.UtcNow < _retryAfterUtc) return;

            var snapshot = _snapshot;
            if (snapshot is not null && snapshot.Lang == lang && snapshot.Generation == _generation) return;

            var generation = _generation;
            _loading = Task.Run(() => LoadAsync(lang, generation));
        }
    }

    /// <summary>Reads one language's names and swaps them in. Never throws.</summary>
    private static async Task LoadAsync(string lang, int generation)
    {
        Snapshot? loaded  = null;
        string?   failure = null;
        try
        {
            loaded = await ReadAsync(lang, generation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex.Message.Split('\n', 2)[0].TrimEnd();
        }

        bool again;
        lock (Gate)
        {
            _loading = null;
            if (loaded is not null)
            {
                _snapshot      = loaded;
                _retryAfterUtc = default;
                _lastFailure   = null;
            }
            else
            {
                // No database yet, or it failed: English, and another try on a later call.
                _retryAfterUtc = DateTime.UtcNow + RetryAfter;
                if (failure is not null && failure == _lastFailure) failure = null;
                else if (failure is not null) _lastFailure = failure;
            }

            // An import finished while this was reading, so what it read may be the old names.
            again = loaded is not null && generation != _generation;
        }

        if (failure is not null)
            try { LoadFailed?.Invoke($"Names in \"{lang}\" could not be read, so English is shown: {failure}"); }
            catch { /* the log's fault is not the load's */ }

        if (loaded is not null)
            try { Changed?.Invoke(); }
            catch { /* nor is a listener's */ }

        if (again) StartLoad(lang);
    }

    /// <summary>One language's rows, or null when there is no database to read yet.</summary>
    private static async Task<Snapshot?> ReadAsync(string lang, int generation)
    {
        await using var conn = _connect();
        if (conn is null) return null;
        await conn.OpenAsync().ConfigureAwait(false);

        // The same statement on both engines: quoted identifiers and one bound parameter.
        await using var cmd = conn.Command("""SELECT "Kind", "Id", "Name" FROM "SdeNames" WHERE "Lang" = @lang""");
        cmd.AddWithValue("@lang", lang);

        var byKind = new Dictionary<long, string>[MaxKind + 1];
        for (var i = 0; i < byKind.Length; i++) byKind[i] = new Dictionary<long, string>();

        await using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await r.ReadAsync().ConfigureAwait(false))
        {
            var kind = r.GetInt32(0);
            if (kind <= 0 || kind > MaxKind) continue;
            byKind[kind][r.GetInt64(1)] = r.GetString(2);
        }

        return new Snapshot(lang, generation, byKind);
    }

    private static DbConnection? DefaultConnect()
    {
        // ⚠️ Never the first to open a SQLite file. Opening one CREATES it, empty, and a zero-byte
        // database is worse than none — see SchemaFingerprint. No file yet means no names yet.
        if (DbEngine.IsSqlite && !File.Exists(AppConfig.GetDbPath())) return null;
        return AppDb.Connect();
    }
}

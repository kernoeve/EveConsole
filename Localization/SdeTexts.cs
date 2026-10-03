using System.Data.Common;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;

namespace EveConsole.Localization;

/// <summary>
/// SDE descriptions — an item's, a faction's, an NPC corporation's — in the interface language, for
/// showing on a screen. The descriptions' counterpart of <see cref="SdeNames"/>.
///
/// <para>⚠️ DISPLAY ONLY, as the names are. Every Description column stays English; nothing may
/// compare, store, send or search on what comes back from here.</para>
///
/// <para>⚠️ Read a row at a time, never loaded. Seven languages of descriptions run to tens of
/// megabytes, which is why they have a table of their own (<see cref="SdeText"/>) rather than rows
/// among the names, which are loaded whole. A screen asks for the one it is about to show; the last
/// few hundred asked for are kept, so going back and forth between items asks the database once.</para>
///
/// <para>Never throws. In English, and in a language the SDE does not carry, it hands the English
/// back at once and never touches the database. Otherwise it answers with one query, and with the
/// English where there is no row — the text reads the same in that language, or the SDE has none —
/// and where the read fails, which is said once through <see cref="LoadFailed"/>:</para>
/// <code>
/// Description = await SdeTexts.GetAsync(SdeTextKind.TypeDescription, typeId, type.Description, ct);
/// </code>
/// </summary>
public static class SdeTexts
{
    /// <summary>
    /// A read that failed, in a sentence for the error log. A delegate because this class knows
    /// nothing about logging; wired in <c>App.axaml.cs</c>, beside <see cref="SdeNames.LoadFailed"/>.
    /// Each different failure is said once.
    /// </summary>
    public static Action<string>? LoadFailed { get; set; }

    /// <summary>
    /// The text in the interface language, or <paramref name="english"/>: in English, in a language
    /// the SDE lacks, where the SDE has no other, and when it cannot be read. Safe from any thread;
    /// a cancelled <paramref name="ct"/> answers the English rather than throwing.
    /// </summary>
    /// <param name="english">The entity's own English column — its Description.</param>
    public static async Task<string> GetAsync(SdeTextKind kind, long id, string english, CancellationToken ct = default)
    {
        var lang = SdeNames.Language;
        if (lang is null) return english;

        var key = new Key(lang, kind, id);
        int generation;
        lock (Gate)
        {
            if (Kept.TryGet(key, out var kept)) return kept ?? english;
            generation = _generation;
        }

        (bool Reached, string? Text) read;
        try
        {
            read = await ReadAsync(key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return english;   // the screen has moved on, which is not a failure
        }
        catch (Exception ex)
        {
            Report(lang, ex);
            return english;
        }

        // No database yet: English, and nothing kept — there will be text once there is one.
        if (!read.Reached) return english;

        lock (Gate)
        {
            // An import landed while this was reading, so what it read may be the old text: it
            // answers this once and is not kept.
            if (generation == _generation) Kept.Add(key, read.Text);
        }
        return read.Text ?? english;
    }

    /// <summary>
    /// Forgets every text kept, after an SDE import has replaced them. Called by
    /// <see cref="SdeNames.Reload"/>, so the one signal an import sends reaches both.
    /// </summary>
    internal static void Reload()
    {
        lock (Gate)
        {
            _generation++;
            Kept.Clear();
        }
    }

    // ── Reading ─────────────────────────────────────────────────────────────────

    /// <summary>How many texts are kept. A screen shows one at a time; this is a few hundred items'
    /// worth of going back and forth.</summary>
    private const int KeepAtMost = 256;

    /// <summary>Different failures said at most, so a fault that words itself differently each time
    /// cannot fill the log.</summary>
    private const int SayAtMost = 16;

    private static readonly object Gate = new();

    // Under Gate.
    private static readonly Recent          Kept     = new(KeepAtMost);
    private static readonly HashSet<string> Said     = new(StringComparer.Ordinal);
    private static int                      _generation;

    /// <summary>How the texts are reached: the configured database, through AppDb. A field rather
    /// than a call so that a harness can point it at a database of its own.</summary>
    private static Func<DbConnection?> _connect = DefaultConnect;

    private readonly record struct Key(string Lang, SdeTextKind Kind, long Id);

    /// <summary>One row, or none; Reached is false when there is no database to read yet.</summary>
    private static async Task<(bool Reached, string? Text)> ReadAsync(Key key, CancellationToken ct)
    {
        await using var conn = _connect();
        if (conn is null) return (false, null);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // The same statement on both engines: quoted identifiers and bound parameters.
        await using var cmd = conn.Command("""SELECT "Text" FROM "SdeTexts" WHERE "Kind" = @kind AND "Id" = @id AND "Lang" = @lang""");
        cmd.AddWithValue("@kind", (int)key.Kind);
        cmd.AddWithValue("@id",   key.Id);
        cmd.AddWithValue("@lang", key.Lang);

        return (true, await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string);
    }

    private static void Report(string lang, Exception ex)
    {
        var failure = ex.Message.Split('\n', 2)[0].TrimEnd();
        lock (Gate)
        {
            if (Said.Count >= SayAtMost || !Said.Add(failure)) return;
        }

        try { LoadFailed?.Invoke($"A description in \"{lang}\" could not be read, so the English is shown: {failure}"); }
        catch { /* the log's fault is not the read's */ }
    }

    private static DbConnection? DefaultConnect()
    {
        // ⚠️ Never the first to open a SQLite file: opening one CREATES it, empty — see SdeNames.
        if (DbEngine.IsSqlite && !File.Exists(AppConfig.GetDbPath())) return null;
        return AppDb.Connect();
    }

    /// <summary>The texts asked for most recently, the oldest let go first. Not thread-safe: used under Gate.</summary>
    private sealed class Recent(int capacity)
    {
        private readonly Dictionary<Key, LinkedListNode<(Key Key, string? Text)>> _byKey = new();
        private readonly LinkedList<(Key Key, string? Text)>                        _order = new();

        /// <summary>A text kept for <paramref name="key"/>; null <paramref name="text"/> when the
        /// answer kept is that there is none.</summary>
        public bool TryGet(Key key, out string? text)
        {
            if (!_byKey.TryGetValue(key, out var node)) { text = null; return false; }
            _order.Remove(node);
            _order.AddFirst(node);
            text = node.Value.Text;
            return true;
        }

        public void Add(Key key, string? text)
        {
            if (_byKey.Remove(key, out var old)) _order.Remove(old);
            _byKey[key] = _order.AddFirst((key, text));
            if (_byKey.Count <= capacity) return;

            var oldest = _order.Last!;
            _order.RemoveLast();
            _byKey.Remove(oldest.Value.Key);
        }

        public void Clear()
        {
            _byKey.Clear();
            _order.Clear();
        }
    }
}

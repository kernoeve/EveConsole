using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EveConsole.Data;
using Npgsql;

namespace EveConsole.Services;

/// <summary>
/// The saved chat and voice logins on a shared database, where every PC that may hold the worker lease can read them.
///
/// <para>⚠️ Not a per-machine secret. The lease moves between PCs whenever its holder stops, and whichever PC takes it has
/// to sign in to Jabber with nobody there to type anything, including a service or a container. DPAPI or a keychain could
/// only be opened by the PC that wrote the entry.</para>
///
/// <para>Encrypted with a key made from the database password, which every client and worker of this database already
/// holds. A copied backup alone opens nothing, as a dump does not contain the password. ⚠️ It keeps nothing from someone
/// who already has the password: they can read the database anyway. Changing the database password means typing these
/// logins again, and every PC of one database has to use the same password.</para>
///
/// <para>⚠️ Not an entity, so that nothing walking the model finds it. The in-app agent sends what it reads to a model
/// provider, and <see cref="Agent.Tools.Data.ReadOnlySql"/> refuses this table by name as well.</para>
/// </summary>
public static class SharedLogins
{
    public const string Table = "ServiceSecrets";

    /// <summary>Marks how a value was sealed, should that ever have to change.</summary>
    private const string Format = "v1";

    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private const int TagBytes = 16;

    /// <summary>
    /// ⚠️ A database password may be a word someone chose, and a stolen backup can be attacked offline for as long as the
    /// thief likes, so this is meant to be slow. The cost is paid once per value per process.
    /// </summary>
    private const int KeyRounds = 600_000;

    private static readonly ConcurrentDictionary<(string Password, string Salt), byte[]> Keys = new();

    private static string DatabasePassword =>
        new NpgsqlConnectionStringBuilder(AppConfig.GetPostgresConnection() ?? "").Password ?? "";

    /// <summary>Whether logins can be saved here: with no database password there is nothing to lock them with.</summary>
    public static bool CanSave => DatabasePassword.Length > 0;

    /// <summary>The login saved under a reference, or null if there is none or it was locked with another database password. Throws if the database cannot answer.</summary>
    public static string? Get(string reference) =>
        Stored(reference) is { } stored ? Open(reference, stored, DatabasePassword) : null;

    /// <summary>Whether something is saved under a reference, whether or not it can be opened. Throws if the database cannot answer.</summary>
    public static bool Has(string reference) => Stored(reference) is not null;

    /// <summary>Saves a login, replacing what was there.</summary>
    public static void Save(string reference, string plain) =>
        Execute($"""
                 INSERT INTO "{Table}" ("Reference", "Value") VALUES (@reference, @value)
                 ON CONFLICT ("Reference") DO UPDATE SET "Value" = EXCLUDED."Value"
                 """, reference, SealForHere(reference, plain));

    /// <summary>Saves a login only if none is saved under the reference.</summary>
    public static void AddIfAbsent(string reference, string plain) =>
        Execute($"""
                 INSERT INTO "{Table}" ("Reference", "Value") VALUES (@reference, @value)
                 ON CONFLICT ("Reference") DO NOTHING
                 """, reference, SealForHere(reference, plain));

    public static void Remove(string reference) =>
        Execute($"""DELETE FROM "{Table}" WHERE "Reference" = @reference""", reference);

    /// <summary>
    /// A login as it is written to the database. ⚠️ Tied to its reference, so that a value copied onto another row does
    /// not open there: nobody should be able to make one server's password be offered to another.
    /// </summary>
    public static string Seal(string reference, string plain, string databasePassword)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var text = Encoding.UTF8.GetBytes(plain);
        var sealedText = new byte[text.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(KeyFor(databasePassword, Convert.ToBase64String(salt)), TagBytes);
        aes.Encrypt(nonce, text, sealedText, tag, Encoding.UTF8.GetBytes(reference));

        return string.Join('.', Format, Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
                           Convert.ToBase64String(sealedText), Convert.ToBase64String(tag));
    }

    /// <summary>The login in a value from <see cref="Seal"/>, or null if this password and reference do not open it.</summary>
    public static string? Open(string reference, string stored, string databasePassword)
    {
        var parts = stored.Split('.');
        if (parts.Length != 5 || parts[0] != Format || databasePassword.Length == 0) return null;

        try
        {
            var nonce = Convert.FromBase64String(parts[2]);
            var sealedText = Convert.FromBase64String(parts[3]);
            var tag = Convert.FromBase64String(parts[4]);
            var text = new byte[sealedText.Length];

            using var aes = new AesGcm(KeyFor(databasePassword, parts[1]), TagBytes);
            aes.Decrypt(nonce, sealedText, tag, text, Encoding.UTF8.GetBytes(reference));
            return Encoding.UTF8.GetString(text);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string SealForHere(string reference, string plain)
    {
        var password = DatabasePassword;
        if (password.Length == 0)
            throw new InvalidOperationException("This login cannot be saved: the database has no password to lock it with.");
        return Seal(reference, plain, password);
    }

    private static byte[] KeyFor(string databasePassword, string salt) =>
        Keys.GetOrAdd((databasePassword, salt), k =>
            Rfc2898DeriveBytes.Pbkdf2(k.Password, Convert.FromBase64String(k.Salt), KeyRounds, HashAlgorithmName.SHA256, KeyBytes));

    private static string? Stored(string reference)
    {
        using var conn = new NpgsqlConnection(AppDb.ConnectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand($"""SELECT "Value" FROM "{Table}" WHERE "Reference" = @reference""", conn);
        cmd.Parameters.AddWithValue("reference", reference);
        return cmd.ExecuteScalar() as string;
    }

    private static void Execute(string sql, string reference, string? value = null)
    {
        using var conn = new NpgsqlConnection(AppDb.ConnectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("reference", reference);
        if (value is not null) cmd.Parameters.AddWithValue("value", value);
        cmd.ExecuteNonQuery();
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace EveConsole.Services;

/// <summary>How a secret is being held, for the UI to state plainly.</summary>
public enum SecretProtection
{
    /// <summary>Encrypted by Windows for this user on this machine.</summary>
    Dpapi,
    /// <summary>Held by the desktop keyring, with only a reference in the config file.</summary>
    LibSecret,
    /// <summary>Neither was available. The value is in the config file as typed.</summary>
    None,
}

/// <summary>
/// Keeps the database password out of config.json.
///
/// <para>The file already sits beside the executable when a portable config is used, which is the
/// point of that feature and also the reason this matters: copying the program folder to another
/// machine, into a backup, or into a support bundle takes the password along with it. Encryption
/// here is not about an attacker who is already running as this user — they can read anything the
/// app can — it is about the secret not travelling with a file that is meant to travel.</para>
///
/// <para>⚠️ Deliberately platform-specific, and deliberately not portable between platforms. A
/// DPAPI blob is bound to one Windows user on one machine and is meaningless anywhere else; a
/// keyring entry does not leave the keyring at all. Carrying a config between a Windows and a
/// Linux install therefore carries everything EXCEPT the password, which then has to be typed
/// once. That is the intended trade, not a defect.</para>
///
/// <para>⚠️ When neither is available — a headless Linux box with no keyring, most obviously —
/// the value is stored as typed rather than the app refusing to work. The UI says which of the
/// three happened, because "your password is protected" is a claim that must never be made
/// loosely.</para>
/// </summary>
public static class SecretStore
{
    private const string DpapiPrefix     = "dpapi:";
    private const string LibSecretPrefix = "libsecret:";

    // Identifies this app's entries in the keyring. The attribute pair is the lookup key.
    private const string KeyringService = "eveconsole";

    /// <summary>What protection this machine can actually provide.</summary>
    public static SecretProtection Available =>
        OperatingSystem.IsWindows()  ? SecretProtection.Dpapi
        : HasSecretTool()            ? SecretProtection.LibSecret
                                     : SecretProtection.None;

    /// <summary>A sentence for the settings screen, in the terms a user cares about.</summary>
    public static string Description => Available switch
    {
        SecretProtection.Dpapi =>
            "The password is encrypted by Windows for your account on this machine. Copying "
            + "config.json elsewhere does not carry a usable password.",
        SecretProtection.LibSecret =>
            "The password is held in your desktop keyring; config.json contains only a reference "
            + "to it.",
        _ =>
            "No keyring was found, so the password is stored in config.json as typed. Install "
            + "libsecret-tools (secret-tool) to have it kept in the keyring instead.",
    };

    /// <summary>
    /// Turns a password into whatever should be written to config.json.
    ///
    /// <para><paramref name="reference"/> distinguishes one stored secret from another; it becomes
    /// the keyring lookup key and is otherwise unused.</para>
    /// </summary>
    public static string Protect(string secret, string reference)
    {
        if (string.IsNullOrEmpty(secret)) return "";

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var blob = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(secret), optionalEntropy: null, DataProtectionScope.CurrentUser);
                return DpapiPrefix + Convert.ToBase64String(blob);
            }

            if (HasSecretTool() && StoreInKeyring(secret, reference))
                return LibSecretPrefix + reference;
        }
        catch
        {
            // Falls through to storing it as typed. A password the user cannot save at all is a
            // worse outcome than one saved unprotected and described as such.
        }

        return secret;
    }

    /// <summary>
    /// Reads back whatever <see cref="Protect"/> wrote, including a plain value from before this
    /// existed.
    ///
    /// <para>⚠️ Returns null rather than throwing when a blob cannot be decrypted — which is the
    /// expected outcome after the config is copied to another machine or another user account.
    /// The caller treats that as "no password configured" and asks for it again.</para>
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;

        try
        {
            if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows()) return null;
                var blob = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(blob, optionalEntropy: null, DataProtectionScope.CurrentUser));
            }

            if (stored.StartsWith(LibSecretPrefix, StringComparison.Ordinal))
                return LookupInKeyring(stored[LibSecretPrefix.Length..]);
        }
        catch
        {
            return null;
        }

        // No prefix: written before there was any protection, or saved on a machine that had
        // none. Usable as it is, and re-protected the next time it is saved.
        return stored;
    }

    /// <summary>True when the stored form is actually protected rather than the raw value.</summary>
    public static bool IsProtected(string? stored) =>
        stored is not null
        && (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal)
            || stored.StartsWith(LibSecretPrefix, StringComparison.Ordinal));

    // ── libsecret, through its command line tool ─────────────────────────────
    //
    // ⚠️ secret-tool rather than P/Invoke into libsecret. Binding the C library would mean
    // marshalling GLib types and a dependency the build has to satisfy on every platform; the
    // tool ships in the same package, is stable, and takes the secret on stdin so it never
    // appears in a command line.

    private static bool? _hasSecretTool;

    private static bool HasSecretTool()
    {
        if (_hasSecretTool is { } known) return known;
        if (OperatingSystem.IsWindows()) return (_hasSecretTool = false).Value;

        try
        {
            using var proc = Process.Start(new ProcessStartInfo("secret-tool")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                ArgumentList           = { "--version" },
            });
            proc?.WaitForExit(3000);
            return (_hasSecretTool = proc is not null).Value;
        }
        catch { return (_hasSecretTool = false).Value; }
    }

    private static bool StoreInKeyring(string secret, string reference)
    {
        var psi = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute       = false,
            CreateNoWindow        = true,
        };
        psi.ArgumentList.Add("store");
        psi.ArgumentList.Add("--label=EVE Console database password");
        psi.ArgumentList.Add("service");
        psi.ArgumentList.Add(KeyringService);
        psi.ArgumentList.Add("key");
        psi.ArgumentList.Add(reference);

        using var proc = Process.Start(psi);
        if (proc is null) return false;

        proc.StandardInput.Write(secret);
        proc.StandardInput.Close();
        proc.WaitForExit(10000);
        return proc.HasExited && proc.ExitCode == 0;
    }

    private static string? LookupInKeyring(string reference)
    {
        var psi = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("lookup");
        psi.ArgumentList.Add("service");
        psi.ArgumentList.Add(KeyringService);
        psi.ArgumentList.Add("key");
        psi.ArgumentList.Add(reference);

        using var proc = Process.Start(psi);
        if (proc is null) return null;

        var value = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10000);

        // ⚠️ Not trimmed of everything: secret-tool prints the secret with no trailing newline,
        // and a password may legitimately end in whitespace. Only the newline the shell would add
        // is removed.
        if (!proc.HasExited || proc.ExitCode != 0) return null;
        return value.TrimEnd('\n');
    }
}

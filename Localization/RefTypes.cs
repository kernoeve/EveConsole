using System.Globalization;

namespace EveConsole.Localization;

/// <summary>
/// Wallet journal entry types — ESI's <c>ref_type</c> — by the names a person reads, in the
/// interface language: RefTypeText holds every type ESI's spec declares, keyed by the type in
/// PascalCase ("redeemed_isk_token" → RedeemedIskToken).
///
/// <para>⚠️ The ref type stays the key everywhere — stored, filtered, compared; only the words
/// are looked up. A type ESI adds later is spelled out from its key, in English, until it has an
/// entry.</para>
/// </summary>
public static class RefTypes
{
    public static string Label(string? refType)
    {
        if (string.IsNullOrEmpty(refType)) return "";
        var key = string.Concat(refType.Split('_', StringSplitOptions.RemoveEmptyEntries)
                                       .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return RefTypeText.ResourceManager.GetString(key, CultureInfo.CurrentUICulture)
            ?? CultureInfo.CurrentCulture.TextInfo.ToTitleCase(refType.Replace('_', ' '));
    }
}

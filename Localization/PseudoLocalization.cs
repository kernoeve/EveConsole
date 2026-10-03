using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text;

namespace EveConsole.Localization;

/// <summary>
/// A fake language for finding what translation will break, run with <c>--pseudo-loc</c>: every
/// string from the resources comes out accented, bracketed and about a third longer —
/// "[Šéţţîñĝš ~~~]" for "Settings".
///
/// <para>Two things show up at a glance. Text still written into the XAML or the code stays plain
/// English among the accented, so whatever has not been moved into the resources yet is plain to
/// see. And German and Russian run about a third longer than English, so a label that clips here
/// clips in those languages; the brackets show where a string was cut.</para>
///
/// <para>Installed by swapping each generated class's ResourceManager for one that rewrites what
/// it returns, before anything has asked for a string. A developer's tool: nothing a user can
/// reach turns it on.</para>
/// </summary>
internal static class PseudoLocalization
{
    public static void Install()
    {
        // Every generated text class: in this namespace, named *Text, holding the lazily created
        // manager the generator writes as `resourceMan`.
        var ns = typeof(PseudoLocalization).Namespace;
        foreach (var type in typeof(PseudoLocalization).Assembly.GetTypes())
        {
            if (type.Namespace != ns || !type.Name.EndsWith("Text", StringComparison.Ordinal)) continue;
            var field = type.GetField("resourceMan", BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.FieldType != typeof(ResourceManager)) continue;
            field.SetValue(null, new Stretching(type.FullName!, type.Assembly));
        }
    }

    private sealed class Stretching(string baseName, Assembly assembly) : ResourceManager(baseName, assembly)
    {
        public override string? GetString(string name, CultureInfo? culture) =>
            base.GetString(name, culture) is { } s ? Stretch(s) : null;
    }

    private const string Plain   = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Accents = "áƀçðéƒĝĥíĵķļɱñóþǫŕšţúṽŵẋýžÅƁÇÐÉƑĜĤÍĴĶĻṀÑÓÞǪŔŠŢÚṼŴẊÝŽ";

    /// <summary>
    /// The pseudo form of one string. Placeholders — "{0}", "{1:N0}" — pass through untouched, since
    /// a mangled one would throw in string.Format; so do access-key underscores.
    /// </summary>
    internal static string Stretch(string s)
    {
        var sb = new StringBuilder(s.Length * 2).Append('[');
        var letters = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '{' && s.IndexOf('}', i) is var end and > 0)
            {
                sb.Append(s, i, end - i + 1);
                i = end;
                continue;
            }
            var at = Plain.IndexOf(ch);
            sb.Append(at >= 0 ? Accents[at] : ch);
            if (char.IsLetter(ch)) letters++;
        }

        // A third again, as German and Russian run.
        var pad = Math.Max(1, (int)Math.Ceiling(letters / 3.0));
        return sb.Append(' ').Append('~', pad).Append(']').ToString();
    }
}

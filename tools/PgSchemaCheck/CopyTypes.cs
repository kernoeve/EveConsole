using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Tools.PgSchemaCheck;

/// <summary>
/// Checks that every property in the model is something the migration's binary COPY can write.
///
/// <para>⚠️ This exists because a sampled test is not a test of the model. The copy was verified
/// against three tables chosen by hand, passed, and then failed on a user's first real migration
/// at the first enum it met: "Writing values of 'AlarmActionKind' is not supported for parameters
/// having no NpgsqlDbType". Nothing about those three tables could have caught it.</para>
///
/// <para>So this walks all 197 entities and every property of each, resolves what EF would
/// actually send to the provider, and fails on anything the writer cannot type. It is static and
/// needs no server, so it runs on every check rather than only when somebody migrates.</para>
/// </summary>
internal static class CopyTypes
{
    /// <summary>
    /// The CLR types Npgsql infers a parameter type for unaided. Anything outside this needs a
    /// converter, and the point of the check is that the model does not quietly acquire one.
    /// </summary>
    private static readonly HashSet<Type> Writable =
    [
        typeof(bool), typeof(byte), typeof(short), typeof(int), typeof(long),
        typeof(float), typeof(double), typeof(decimal),
        typeof(string), typeof(char),
        typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(Guid),
        typeof(byte[]),
    ];

    public static int Check()
    {
        const string designTime = "Host=schema.check.invalid;Database=eveconsole;Username=none";
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(designTime).Options;
        using var db = new AppDbContext(opts);

        var failures = 0;
        var converted = 0;
        var checkedProps = 0;

        foreach (var entity in db.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.PropertyInfo is null) continue;   // shadow: the copy skips it too
                checkedProps++;

                var converter = property.GetTypeMapping().Converter;
                if (converter is not null) converted++;

                // What the copy will actually hand the writer: the converter's output when there
                // is one, otherwise the property's own type.
                var providerType = Nullable.GetUnderlyingType(
                        converter?.ProviderClrType ?? property.ClrType)
                    ?? converter?.ProviderClrType ?? property.ClrType;

                if (providerType.IsEnum)
                {
                    Console.Error.WriteLine(
                        $"  UNWRITABLE: {entity.GetTableName()}.{property.GetColumnName()} "
                        + $"reaches the writer as enum {providerType.Name}");
                    failures++;
                }
                else if (!Writable.Contains(providerType))
                {
                    Console.Error.WriteLine(
                        $"  UNWRITABLE: {entity.GetTableName()}.{property.GetColumnName()} "
                        + $"reaches the writer as {providerType.Name}, which Npgsql cannot type");
                    failures++;
                }
            }
        }

        Console.WriteLine($"\nCopy types: {checkedProps:N0} properties across "
                          + $"{db.Model.GetEntityTypes().Count():N0} entities, "
                          + $"{converted:N0} through a value converter.");
        if (failures == 0) Console.WriteLine("  all writable by binary COPY.");
        return failures;
    }
}

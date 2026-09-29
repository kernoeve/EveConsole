using System.Collections.Concurrent;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Fitting;

public sealed record DogmaAttributeInfo(
    int Id, string Name, double DefaultValue, bool Stackable, bool HighIsGood, int? MinAttributeId, int? MaxAttributeId);

public sealed record DogmaModifierInfo(
    string Func, string Domain, int Operation, int ModifiedAttributeId, int ModifyingAttributeId, int? GroupId, int? SkillTypeId);

public sealed record DogmaEffectInfo(
    int Id, string Name, int Category,
    int? DurationAttributeId, int? DischargeAttributeId, int? RangeAttributeId,
    int? FalloffAttributeId, int? TrackingSpeedAttributeId, int? ResistanceAttributeId,
    IReadOnlyList<DogmaModifierInfo> Modifiers);

/// <summary>One type as the engine sees it: its dogma attributes, effects and required skills.</summary>
public sealed class DogmaTypeInfo
{
    public required int    Id         { get; init; }
    public required string Name       { get; init; }
    public required int    GroupId    { get; init; }
    public required int    CategoryId { get; init; }
    public required IReadOnlyDictionary<int, double> Attributes { get; init; }
    public required IReadOnlyList<int> EffectIds { get; init; }
    public int? DefaultEffectId { get; init; }
    /// <summary>requiredSkill1..6, the skills an item "requires" for LocationRequiredSkill and
    /// OwnerRequiredSkill modifiers. Levels are not part of that match.</summary>
    public required IReadOnlySet<int> RequiredSkills { get; init; }

    public bool Requires(int skillTypeId) => RequiredSkills.Contains(skillTypeId);
    public double? Attr(int id) => Attributes.TryGetValue(id, out var v) ? v : null;
}

/// <summary>
/// The static half of the fitting engine: every dogma attribute and effect, loaded once, and
/// types loaded as a fit asks for them. Read-only after load, so one instance serves every fit.
/// </summary>
public sealed class DogmaData
{
    // Categories the engine treats specially.
    public const int CategoryShip      = 6;
    public const int CategoryModule    = 7;
    public const int CategoryCharge    = 8;
    public const int CategorySkill     = 16;
    public const int CategoryDrone     = 18;
    public const int CategoryImplant   = 20;
    public const int CategorySubsystem = 32;
    public const int CategoryFighter   = 87;
    public const int CategoryStructure = 65;
    public const int CategoryStructureModule = 66;

    // Attributes read by id rather than name because the engine itself depends on them.
    public const int AttrMass        = 4;
    public const int AttrCapacity    = 38;
    public const int AttrVolume      = 161;
    public const int AttrRadius      = 162;
    public const int AttrSkillLevel  = 280;
    private static readonly int[] RequiredSkillAttrs = [182, 183, 184, 1285, 1289, 1290];

    public IReadOnlyDictionary<int, DogmaAttributeInfo>    Attributes       { get; }
    public IReadOnlyDictionary<string, DogmaAttributeInfo> AttributesByName { get; }
    public IReadOnlyDictionary<int, DogmaEffectInfo>       Effects          { get; }
    public IReadOnlyDictionary<string, DogmaEffectInfo>    EffectsByName    { get; }
    /// <summary>Every published skill, for an "all skills at level N" character.</summary>
    public IReadOnlyList<int> SkillTypeIds { get; }
    public int SdeBuild { get; }

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ConcurrentDictionary<int, DogmaTypeInfo> _types = new();

    private DogmaData(IDbContextFactory<AppDbContext> dbFactory,
        Dictionary<int, DogmaAttributeInfo> attributes, Dictionary<int, DogmaEffectInfo> effects,
        List<int> skills, int sdeBuild)
    {
        _dbFactory       = dbFactory;
        Attributes       = attributes;
        AttributesByName = attributes.Values.GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.First());
        Effects          = effects;
        EffectsByName    = effects.Values.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First());
        SkillTypeIds     = skills;
        SdeBuild         = sdeBuild;
    }

    public static async Task<DogmaData> LoadAsync(IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var attributes = (await db.SdeDogmaAttributes.AsNoTracking().ToListAsync(ct))
            .ToDictionary(a => a.AttributeId, a => new DogmaAttributeInfo(
                a.AttributeId, a.Name, a.DefaultValue, a.Stackable, a.HighIsGood, a.MinAttributeId, a.MaxAttributeId));

        var modifiers = (await db.SdeDogmaEffectModifiers.AsNoTracking().ToListAsync(ct))
            .Where(m => m.Operation is not null && m.ModifiedAttributeId is not null && m.ModifyingAttributeId is not null)
            .GroupBy(m => m.EffectId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DogmaModifierInfo>)g.OrderBy(m => m.Ordinal)
                .Select(m => new DogmaModifierInfo(m.Func, m.Domain, m.Operation!.Value,
                    m.ModifiedAttributeId!.Value, m.ModifyingAttributeId!.Value, m.GroupId, m.SkillTypeId))
                .ToList());

        var effects = (await db.SdeDogmaEffects.AsNoTracking().ToListAsync(ct))
            .ToDictionary(e => e.EffectId, e => new DogmaEffectInfo(
                e.EffectId, e.Name, e.EffectCategory,
                e.DurationAttributeId, e.DischargeAttributeId, e.RangeAttributeId,
                e.FalloffAttributeId, e.TrackingSpeedAttributeId, e.ResistanceAttributeId,
                modifiers.GetValueOrDefault(e.EffectId) ?? []));

        var skills = await (from t in db.SdeTypes.AsNoTracking()
                            join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                            where g.CategoryId == CategorySkill && t.Published
                            select t.TypeId).ToListAsync(ct);

        var build = await db.SdeBuildInfos.AsNoTracking().Select(b => b.BuildNumber).FirstOrDefaultAsync(ct);
        return new DogmaData(dbFactory, attributes, effects, skills, build);
    }

    public DogmaAttributeInfo? Attribute(string name) => AttributesByName.GetValueOrDefault(name);
    public int AttrId(string name) => AttributesByName.TryGetValue(name, out var a) ? a.Id
        : throw new KeyNotFoundException($"dogma attribute '{name}' is not in the SDE");

    /// <summary>A type already loaded by <see cref="LoadTypesAsync"/>.</summary>
    public DogmaTypeInfo Type(int typeId) => _types.TryGetValue(typeId, out var t) ? t
        : throw new InvalidOperationException($"type {typeId} has not been loaded");

    public bool TryType(int typeId, out DogmaTypeInfo type) => _types.TryGetValue(typeId, out type!);

    /// <summary>Loads (once) every type in <paramref name="typeIds"/> not already held.</summary>
    public async Task LoadTypesAsync(IEnumerable<int> typeIds, CancellationToken ct = default)
    {
        var missing = typeIds.Where(id => !_types.ContainsKey(id)).Distinct().ToList();
        if (missing.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var chunk in missing.Chunk(500))
        {
            var ids = chunk.ToList();
            var types = await (from t in db.SdeTypes.AsNoTracking()
                               join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                               where ids.Contains(t.TypeId)
                               select new { t.TypeId, t.Name, t.GroupId, g.CategoryId, t.Mass, t.Capacity, t.Volume, t.Radius })
                              .ToListAsync(ct);
            var attrs = (await db.SdeTypeDogmaAttributes.AsNoTracking().Where(a => ids.Contains(a.TypeId)).ToListAsync(ct))
                .ToLookup(a => a.TypeId);
            var effs = (await db.SdeTypeDogmaEffects.AsNoTracking().Where(e => ids.Contains(e.TypeId)).ToListAsync(ct))
                .ToLookup(e => e.TypeId);

            foreach (var t in types)
            {
                var a = attrs[t.TypeId].ToDictionary(x => x.AttributeId, x => x.Value);
                // Mass, capacity and volume live on the type row, not in typeDogma, and the
                // engine modifies all three (plates add mass, expanders add capacity).
                a.TryAdd(AttrMass,     t.Mass);
                a.TryAdd(AttrCapacity, t.Capacity);
                a.TryAdd(AttrVolume,   t.Volume);
                if (t.Radius > 0) a.TryAdd(AttrRadius, t.Radius);

                var required = RequiredSkillAttrs
                    .Where(a.ContainsKey).Select(id => (int)a[id]).Where(id => id > 0).ToHashSet();

                _types[t.TypeId] = new DogmaTypeInfo
                {
                    Id = t.TypeId, Name = t.Name, GroupId = t.GroupId, CategoryId = t.CategoryId,
                    Attributes = a,
                    EffectIds = effs[t.TypeId].Select(e => e.EffectId).ToList(),
                    DefaultEffectId = effs[t.TypeId].FirstOrDefault(e => e.IsDefault)?.EffectId,
                    RequiredSkills = required,
                };
            }
        }
    }

    /// <summary>Type ids for names, case-insensitive, published types preferred.</summary>
    public async Task<Dictionary<string, int>> FindTypesByNameAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        var wanted = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return found;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var chunk in wanted.Chunk(500))
        {
            var list = chunk.ToList();
            var rows = await db.SdeTypes.AsNoTracking()
                .Where(t => list.Contains(t.Name))
                .Select(t => new { t.TypeId, t.Name, t.Published }).ToListAsync(ct);
            foreach (var r in rows.OrderByDescending(r => r.Published))
                found.TryAdd(r.Name, r.TypeId);
        }
        return found;
    }
}

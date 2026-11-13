// Checks the sums behind the Worklist's structure tasks and the Corp Structures list: when fuel
// is due, how much to bring, when a moon bay is worth collecting, and that an expiry read from a
// list no longer polled is shown as unknown rather than out of fuel. No database. A wrong answer
// here sends someone to fuel a structure that was fuelled weeks ago, or leaves one to go dark.
using System.Globalization;
using EveConsole.Services;
using EveConsole.Services.Worklist;
using EveConsole.ViewModels;

CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

var failures = 0;
var passes = 0;

void Check(bool ok, string what)
{
    if (ok) passes++;
    else { failures++; Console.WriteLine("FAILED: " + what); }
}

void Same<T>(T actual, T wanted, string what) => Check(Equals(actual, wanted), $"{what}: got {actual}, wanted {wanted}");

// ---- Fuel due ----
{
    Check(StructureTasks.FuelDue(3, 7),   "three days left, seven the minimum: due");
    Check(!StructureTasks.FuelDue(10, 7), "ten days left: not yet");
    Check(StructureTasks.FuelDue(0, 0),   "out of fuel is due even with no minimum");
    Check(!StructureTasks.FuelDue(7, 7),  "exactly the minimum left: not yet");
}

// ---- Fuel to bring ----
{
    // 5 blocks an hour, 3 days left, fill to 30: 27 days × 24 × 5.
    Same(StructureTasks.FuelToBring(3, 30, 5), 3240L, "27 days at 5 an hour");
    Same(StructureTasks.FuelToBring(31, 30, 5), 0L, "already past the fill level: nothing");
    Same(StructureTasks.FuelToBring(0, 30, 41), 29520L, "out of fuel: the whole fill, at a Tatara's 41");
    Same(StructureTasks.FuelToBring(29.99, 30, 5), 2L, "a part block is a whole one");
    Same(StructureTasks.FuelToBring(3, 30, 0), 0L, "nothing burns: nothing to bring");
    Same(StructureTasks.FuelToBring(-2, 30, 1), 720L, "a negative left counts as none");
}

// ---- Moon bay pickup ----
{
    const double cap = 500_000;
    Check(StructureTasks.PickupDue(StructurePickupMeasure.PercentFull, 1, 0, 5_567, cap), "1.1% full against 1%");
    Check(!StructureTasks.PickupDue(StructurePickupMeasure.PercentFull, 50, 0, 5_567, cap), "1.1% full against 50%");
    Check(StructureTasks.PickupDue(StructurePickupMeasure.Volume, 5_000, 0, 5_567, cap), "5,567 m³ against 5,000");
    Check(StructureTasks.PickupDue(StructurePickupMeasure.MarketValue, 100e6, 125.8e6, 5_567, cap), "125.8M against 100M");
    Check(!StructureTasks.PickupDue(StructurePickupMeasure.MarketValue, 200e6, 125.8e6, 5_567, cap), "125.8M against 200M");
    Check(!StructureTasks.PickupDue(StructurePickupMeasure.Volume, 0, 0, 0, cap), "an empty bay is never due");
    Same(Math.Round(StructureTasks.PercentFull(5_567, cap), 2), 1.11, "percent full");
}

// ---- A list no longer read ----
{
    var now = DateTimeOffset.UtcNow;
    var expired = new CorpStructureInfo
    {
        Kind = CorpStructureKind.Upwell, Name = "Bridge", TypeName = "Ansiblex Jump Bridge",
        FuelExpires = now.AddDays(-5), RunsOut = now.AddDays(-5), IsPowered = false, NeedsFuel = true,
    };
    var readLongAgo = new CorpListRead(now.AddDays(-15), Denied: true, Stale: true);

    var current = new CorpStructureRowVm(expired, now, new CorpListRead(now.AddMinutes(-20), false, false));
    Same(current.FuelLeft, "Out of fuel", "a current list: out of fuel is out of fuel");
    Check(current.IsCritical, "and red");

    var stale = new CorpStructureRowVm(expired, now, readLongAgo);
    Check(stale.FuelUnknown, "a stale list: the passed expiry is unknown");
    Same(stale.FuelLeft, "—", "shown as a dash");
    Check(!stale.IsCritical, "and not red");
    Check(stale.FuelTip?.Contains("last read") == true, "the tooltip says when it was read: " + stale.FuelTip);

    var fine = new CorpStructureInfo
    {
        Kind = CorpStructureKind.Upwell, Name = "Citadel", TypeName = "Fortizar",
        FuelExpires = now.AddDays(20), RunsOut = now.AddDays(20), IsPowered = true, NeedsFuel = true,
    };
    var later = new CorpStructureRowVm(fine, now, readLongAgo);
    Check(!later.FuelUnknown, "a stale list: an expiry still ahead is shown as it was");

    // Out of fuel first, then unknown, then least left.
    var sorted = new[] { later, stale, current }.OrderBy(r => r.FuelSort).ToList();
    Check(sorted[0] == current && sorted[1] == stale && sorted[2] == later,
          "out of fuel sorts first, unknown next, then the time left");
}

// ---- When the state ends ----
{
    var now = DateTimeOffset.UtcNow;
    var reinforced = new CorpStructureInfo
    {
        Kind = CorpStructureKind.Upwell, Name = "Citadel", State = "armor_reinforce",
        StateTimerStart = now.AddHours(-10), StateTimerEnd = now.AddHours(30), IsPowered = true,
    };
    var row = new CorpStructureRowVm(reinforced, now);
    Check(row.StateEnds.Contains("in 1d 6h"), "a reinforcement timer's end and how far: " + row.StateEnds);
    Same(row.StateEndsSort, reinforced.StateTimerEnd!.Value, "sorted by the timer's end");

    var unanchoring = new CorpStructureInfo
    {
        Kind = CorpStructureKind.Upwell, Name = "Old", State = "shield_vulnerable",
        UnanchorsAt = now.AddDays(3), IsPowered = true,
    };
    var u = new CorpStructureRowVm(unanchoring, now);
    Check(u.StateText.Contains("unanchoring"), "unanchoring is in the state: " + u.StateText);
    Check(u.StateEnds.Length > 0, "and when it ends");

    var tower = new CorpStructureInfo
    {
        Kind = CorpStructureKind.Tower, Name = "Moon", State = "reinforced",
        ReinforcedUntil = now.AddHours(20), IsPowered = true,
    };
    Same(new CorpStructureRowVm(tower, now).StateEndsSort, tower.ReinforcedUntil!.Value, "a tower's reinforcement end");

    var calm = new CorpStructureInfo { Kind = CorpStructureKind.Upwell, Name = "Calm", State = "shield_vulnerable", IsPowered = true };
    Same(new CorpStructureRowVm(calm, now).StateEnds, "", "no timer: nothing shown");
}

Console.WriteLine(failures == 0
    ? $"PASSED: structure task check: all {passes} passed."
    : $"FAILED: structure task check: {failures} failed, {passes} passed.");
return failures == 0 ? 0 : 1;

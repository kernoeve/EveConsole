// Checks what a Buy listing that takes only what is needed asks for, says and sends: the need
// worked out from the plan and the open offers, the mail price list's lines, and the catalogue the
// web site caps offers by. Constructed figures only: no database, no site, no mail. A wrong answer
// here takes more of an item than the plan wants, or turns away a seller the plan is waiting on.
using System.Globalization;
using System.Text.Json;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.WebStore;
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

// ---- The need ----
{
    var n = new ItemNeed(Wanted: 100, OnHand: 30, InBuild: 20, OnOrder: 10);
    Same(n.Needed, 40L, "wanted less held, in build and offered");
    Same((n with { OnOrder = 80 }).Needed, 0L, "never below nothing");
    Same(ItemNeed.None.Needed, 0L, "nothing wanted, nothing needed");

    // From the plan's total: what it has includes what is in build, which is shown on its own.
    var fromPlan = StoreNeedService.Of(new DemandTotal(Wanted: 100, Have: 50, InBuild: 20), onOrder: 10);
    Same(fromPlan, new ItemNeed(100, 30, 20, 10), "held is what the plan has less what is in build");
    Same(fromPlan.Needed, 40L, "and the need is the same sum");
    Same(StoreNeedService.Of(new DemandTotal(10, 5, 9), 0).OnHand, 0L, "more in build than counted had: none held, not fewer");
    // The plan counts open offers as on their way; the need counts them once, as On Order.
    var withOffers = StoreNeedService.Of(new DemandTotal(Wanted: 100, Have: 60, InBuild: 20, Incoming: 10), onOrder: 10);
    Same(withOffers, new ItemNeed(100, 30, 20, 10), "offers in the plan's have are On Order, not On Hand");
    Same(withOffers.Needed, 40L, "and are taken off once, not twice");

    // The Worklist's view of what exists includes them, so it does not build what is coming.
    var stock = new ScopeStock(
        new Dictionary<(int TypeId, long OwnerId), long> { [(1, 98000001)] = 30 },
        new Dictionary<(int TypeId, long OwnerId), long>(),
        InBuild: new Dictionary<int, long> { [1] = 20 },
        Incoming: new Dictionary<int, long> { [1] = 10 });
    Same(stock.Anywhere(1), 60L, "held, in build and offered all exist for the plan");
    Same(stock.OnOffer(1), 10L, "the offered part, on its own");
    Same(stock.Anywhere(2), 0L, "an item nobody has or offers");

    var unplanned = StoreNeedService.Of(null, onOrder: 5);
    Same(unplanned.Needed, 0L, "an item the plan doesn't use is not needed");
    Same(unplanned.OnOrder, 5L, "though its open offers still show");
}

// ---- Open offers ----
{
    TrackedOrder Order(int type, int units, int delivered = 0, string side = StorePosting.Buy, string status = "pending", int store = 7) =>
        new() { TypeId = type, Units = units, UnitsDelivered = delivered, Side = side, Status = status, StoreId = store };

    var offered = StoreNeedService.Offered(
    [
        Order(1, 50),
        Order(1, 30, delivered: 10),
        Order(1, 20, delivered: 25),            // delivered more than offered: counts none, not less
        Order(1, 99, status: "completed"),
        Order(1, 99, status: "canceled"),
        Order(1, 99, side: StorePosting.Sell),  // someone buying from us is no offer
        Order(1, 99, store: 8),                 // another store's
        Order(2, 5),
    ], storeId: 7);
    Same(offered.GetValueOrDefault(1), 70L, "this store's open offers, less what they delivered");
    Same(offered.GetValueOrDefault(2), 5L, "by item");
    Check(!offered.ContainsKey(3), "an item with no offers has none");
}

// ---- The mail price list ----
PostingItemView Item(int type, string name, ItemNeed? need = null) =>
    new(type, name, null, null, null, 0, 0, 0, null, null, null, 1_000_000, null, Need: need);

PostingView View(bool showNeed, params PostingItemView[] items) =>
    new(false, false, false, false, false, "#4a9a5a", "#c8a84b", "#888899",
        [new PostingSectionView("Parts", "", null, null, items)], showNeed);

string Lines(PostingView v) =>
    SalePostingRenderer.Detail(v, OutputFormat.ByName("Plain Text"), new SalePostingPost { PostType = "Detail" });
{
    var still = Item(1, "Capital Part A", new ItemNeed(100, 30, 20, 10));
    var done  = Item(2, "Capital Part B", new ItemNeed(10, 10, 0, 0));
    var free  = Item(3, "Capital Part C");

    var shown = Lines(View(true, still, done, free));
    var line  = shown.Split('\n');
    Check(line.Single(l => l.Contains("Part A")).Contains("40 still needed"), "a needed item says how many more: " + shown);
    Check(line.Single(l => l.Contains("Part B")).Contains("no longer needed"), "one no longer needed says so");
    Check(!line.Single(l => l.Contains("Part C")).Contains("needed"), "an item with no need says nothing of it");

    var hidden = Lines(View(false, still, done, free)).Split('\n');
    Check(!hidden.Single(l => l.Contains("Part A")).Contains("still needed"), "the figures kept to the store when not shown");
    Check(hidden.Single(l => l.Contains("Part B")).Contains("no longer needed"), "but an item no longer needed is still marked");

    // Given needs: an item the plan has nothing for wants nothing.
    var given = View(true, Item(1, "Capital Part A"), Item(4, "Capital Part D"))
        .WithNeeds(new Dictionary<int, ItemNeed> { [1] = new ItemNeed(5, 0, 0, 0) });
    var items = given.Sections.SelectMany(s => s.Items).ToDictionary(i => i.TypeId);
    Same(items[1].Need?.Needed, 5L, "an item takes its need");
    Check(items[4].NotNeeded, "an item missing from the plan is not needed");
    Check(given.TypeIds.SequenceEqual([1, 4]), "the view names its items");
}

// ---- What the site is sent ----
{
    var item = new CatalogueItemDto { TypeId = 1, Name = "Capital Part A", UnitPrice = 1_000_000 };
    var plain = JsonSerializer.Serialize(item, WebStoreProtocol.Json);
    Check(!plain.Contains("need"), "an item without a need sends none, which is no cap: " + plain);

    item.Need = NeedDto.Of(new ItemNeed(100, 30, 20, 10));
    var json = JsonDocument.Parse(JsonSerializer.Serialize(item, WebStoreProtocol.Json)).RootElement.GetProperty("need");
    Same(json.GetProperty("needed").GetInt64(), 40L, "needed, as the site reads it");
    Same(json.GetProperty("wanted").GetInt64(), 100L, "wanted");
    Same(json.GetProperty("onHand").GetInt64(), 30L, "onHand");
    Same(json.GetProperty("inBuild").GetInt64(), 20L, "inBuild");
    Same(json.GetProperty("onOrder").GetInt64(), 10L, "onOrder");

    // The site skips a write when the hash is unchanged, so a change of need has to change it…
    CatalogueDto Catalogue(long needed, bool? showNeed) => new()
    {
        ShowNeed = showNeed,
        Sections = [new CatalogueSectionDto { Name = "Parts", Items = [new CatalogueItemDto
        {
            TypeId = 1, Name = "Capital Part A", UnitPrice = 1_000_000,
            Need = new NeedDto { Needed = needed, Wanted = 100 },
        }] }],
    };
    Check(StoreCatalogueBuilder.HashOf(Catalogue(40, true)) != StoreCatalogueBuilder.HashOf(Catalogue(39, true)), "a change of need changes the hash");
    Check(StoreCatalogueBuilder.HashOf(Catalogue(40, true)) != StoreCatalogueBuilder.HashOf(Catalogue(40, false)), "so does showing it");

    // …and a listing that doesn't track need must hash as it always did, or every store rewrites.
    var old = new CatalogueDto { Sections = [new CatalogueSectionDto { Name = "Parts", Items = [new CatalogueItemDto { TypeId = 1, Name = "X" }] }] };
    var before = JsonSerializer.Serialize(new
    {
        old.ShowInStock, old.ShowInBuild, old.ShowReserved, old.ShowCompletionDate,
        old.ColourByState, old.ColourInStock, old.ColourInBuild, old.ColourNone,
        old.Sections,
    }, WebStoreProtocol.Json);
    var hashBefore = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(before))).ToLowerInvariant();
    Same(StoreCatalogueBuilder.HashOf(old), hashBefore, "a catalogue without need hashes as before");
}

// ---- Where Buy contracts are made: several places, and the one a store had before ----
{
    // A store from before there could be several: its one place, kept.
    var old = new Store { BuyLocationId = 60003760, BuyLocationName = "Station One" };
    Same(StoreBuyLocations.Of(old).Single(), new BuyLocation(60003760, "Station One"), "the one place a store had is its list");
    Same(StoreBuyLocations.Of(new Store()).Count, 0, "a store that named none names none");

    // Adding a second keeps the first, in both forms.
    StoreBuyLocations.Set(old, [new BuyLocation(60003760, "Station One"), new BuyLocation(1_000_000_000_001, "Structure Two")]);
    var both = StoreBuyLocations.Of(old);
    Check(both.Select(b => b.Id).SequenceEqual([60003760L, 1_000_000_000_001L]), "both places, in order");
    Same(old.BuyLocationId, 60003760L, "the first stays in the old column, for an older app");
    Same(old.BuyLocationName, "Station One", "with its name");

    // Removing the first moves the next into the old column; none clears everything.
    StoreBuyLocations.Set(old, [new BuyLocation(1_000_000_000_001, "Structure Two")]);
    Same(old.BuyLocationId, 1_000_000_000_001L, "the next place becomes the one an older app reads");
    StoreBuyLocations.Set(old, []);
    Same(StoreBuyLocations.Of(old).Count, 0, "none left");
    Same(old.BuyLocationId, 0L, "and none in the old column, or it would come back");

    // One place twice is one place; unreadable text falls back to the old column.
    StoreBuyLocations.Set(old, [new BuyLocation(5, "A"), new BuyLocation(5, "A"), new BuyLocation(0, "nothing")]);
    Same(StoreBuyLocations.Of(old).Count, 1, "a place named twice, or no place, is not kept");
    var broken = new Store { BuyLocations = "{not json", BuyLocationId = 7, BuyLocationName = "Seven" };
    Same(StoreBuyLocations.Of(broken).Single().Id, 7L, "unreadable: the old column still answers");

    Same(StoreBuyLocations.Phrase(["A"]), "A", "one place");
    Same(StoreBuyLocations.Phrase(["A", "B"]), "A or B", "two: either");
    Same(StoreBuyLocations.Phrase(["A", "B", "C"]), "A, B, or C", "three: any of them, worded as the site words it");
    using (LanguageScope.Use("ja"))
    {
        Same(StoreBuyLocations.Phrase(["A", "B"]), "AまたはB", "Japanese, two");
        Same(StoreBuyLocations.Phrase(["A", "B", "C"]), "A、B、またはC", "Japanese, three, as the site has it");
    }
    using (LanguageScope.Use("de"))
        Same(StoreBuyLocations.Phrase(["A", "B", "C"]), "A, B oder C", "German, three");
    Same(StoreBuyLocations.Phrase([]), "", "none");
}

// ---- A listing ----
{
    Check(new StorePosting { Side = StorePosting.Buy, TrackNeed = true }.CapsAtNeed, "a Buy listing tracking need caps");
    Check(!new StorePosting { Side = StorePosting.Sell, TrackNeed = true }.CapsAtNeed, "a Sell listing never does");
    Check(!new StorePosting { Side = StorePosting.Buy }.CapsAtNeed, "nor a Buy listing that doesn't track it");
}

// ---- An offer's expected contract date ----
{
    Same(WebStoreSyncService.OfferDate("2026-12-01"), "2026-12-01", "a picked date is the order's expected date");
    Same(WebStoreSyncService.OfferDate(" 2026-12-01 "), "2026-12-01", "trimmed");
    Same(WebStoreSyncService.OfferDate(null), null, "none sent, none set");
    Same(WebStoreSyncService.OfferDate(""), null, "blank, none set");
    Same(WebStoreSyncService.OfferDate("2026-02-30"), null, "not a real date");
    Same(WebStoreSyncService.OfferDate("12/01/2026"), null, "only the site's own form");
}

Console.WriteLine(failures == 0
    ? $"PASSED: store need check: all {passes} passed."
    : $"FAILED: store need check: {failures} failed, {passes} passed.");
return failures == 0 ? 0 : 1;

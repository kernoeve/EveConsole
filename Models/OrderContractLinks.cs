namespace EveConsole.Models;

/// <summary>
/// One contract an order is on, and how many of the order's units it carries — null while the
/// fulfilment pass has yet to count them: a contract attached by hand, until the pass can read
/// what the contract holds.
/// </summary>
public readonly record struct ContractLink(int ContractId, int? Units);

/// <summary>
/// Reads and writes <see cref="TrackedOrder.LinkedContracts"/>, and the questions every reader
/// of it asks.
///
/// <para>Entries are "id:units", comma separated, oldest first: "236432592:1,236455120:1". A bare
/// id is one attached by hand whose units are still to be counted.</para>
/// </summary>
public static class OrderContractLinks
{
    /// <summary>
    /// An order's contracts.
    ///
    /// <para>⚠️ An order linked before an order could have several contracts carries only
    /// LinkedContractId, and that link meant the whole order. It reads as that one contract with
    /// its units still to count — the pass then counts them, and a settled order keeps meaning
    /// the whole contract, as it did when it was settled.</para>
    /// </summary>
    public static List<ContractLink> Of(TrackedOrder o) => Parse(o.LinkedContracts, o.LinkedContractId);

    public static List<ContractLink> Parse(string links, int? legacy)
    {
        var list = new List<ContractLink>();
        foreach (var part in links.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(':');
            if (!int.TryParse(bits[0], out var id) || id <= 0 || list.Any(l => l.ContractId == id)) continue;
            int? units = bits.Length > 1 && int.TryParse(bits[1], out var u) && u >= 0 ? u : null;
            list.Add(new ContractLink(id, units));
        }
        if (list.Count == 0 && legacy is > 0 and int only) list.Add(new ContractLink(only, null));
        return list;
    }

    public static string Format(IEnumerable<ContractLink> links) =>
        string.Join(",", links.Select(l => l.Units is int u ? $"{l.ContractId}:{u}" : $"{l.ContractId}"));

    /// <summary>The contract ids alone, oldest first.</summary>
    public static List<int> Ids(TrackedOrder o) => Of(o).Select(l => l.ContractId).ToList();

    /// <summary>
    /// Linked before the fulfilment pass counted units — a link from an older version, which
    /// meant the whole order. Only ever true between an upgrade and the first pass.
    /// </summary>
    public static bool IsUncounted(TrackedOrder o) =>
        o.LinkedContractId is not null && o.LinkedContracts.Length == 0
        && o.UnitsDelivered == 0 && o.UnitsContracted == 0;

    /// <summary>
    /// Units still to come from the shelf or a build: ordered, less those delivered and those on
    /// a contract waiting to be accepted. Zero for a settled order.
    /// </summary>
    public static int StillToSupply(TrackedOrder o) =>
        o.Status != "pending" || IsUncounted(o)
            ? 0
            : Math.Max(0, o.Units - o.UnitsDelivered - o.UnitsContracted);

    /// <summary>
    /// A contract is made out and waiting for the buyer — the one state where the next move is
    /// theirs. LinkedContractId is then that contract.
    /// </summary>
    public static bool AwaitsAcceptance(TrackedOrder o) =>
        o.Status == "pending" && o.LinkedContractId is not null
        && (o.UnitsContracted > 0 || IsUncounted(o));
}

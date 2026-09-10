namespace EveConsole.Agent;

/// <summary>
/// What the generated schema cannot say.
///
/// <para><see cref="AgentSchema"/> lists every table and describe_tables gives exact columns, so
/// nothing here repeats a column list — that was the old mistake, and a stale one is worse than
/// none: MarketItemPrices was documented as BuyMax/SellMin, the real columns are
/// BuyPrice/SellPrice/Midpoint, and two queries failed on it before describe_tables told the
/// truth. Incomplete notes make the agent look; wrong notes make it fail with confidence.</para>
///
/// <para>So this carries only the three things generation cannot produce: which table answers
/// which QUESTION, what a coded value MEANS, and the traps where a query returns a plausible
/// number that is wrong. Every claim here was checked against the live database.</para>
/// </summary>
public static class AgentDataNotes
{
    public const string Notes = """
        # Data notes

        Column names come from describe_tables and nothing here restates them. These are the
        meanings, the joins that answer real questions, and the places a query returns a number
        that looks right and is not.

        ## Who someone is, and who they WERE
        ⚠️ These two tables answer different questions and swapping them gives a confident wrong
        answer. Read this before answering anything about who is in which corporation.

        - EsiCorpMembers is the CURRENT roster of a corporation the capsuleer has roles in, and it
          is the right answer to "is X still in the corp", "who has left", "which of these buyers
          are still with us". EsiCorpMemberTracking adds when each member joined and last logged
          in. Someone absent from EsiCorpMembers who appears in older data has left.

        - ⚠️ CharacterAffiliations is a FIRST-SEEN CACHE from INTEL REPORTS ONLY, and it is both
          stale and sparse. A row is written the first time a character id appears in a parsed
          intel channel, and is NEVER refreshed afterwards. Nothing else writes to it.

          Two consequences, and the second is the one that catches people out:
          · Rows are old. Most are months old and will stay that way. PulledAt is when the
            character was first SEEN, not when their corporation was last checked — the name
            invites exactly the opposite reading.
          · ABSENCE MEANS NOTHING. A character with no row was simply never reported in intel.
            It does NOT mean they have no corporation, are not in an alliance, or have left one.
            Contract counterparties, market counterparties and mail senders are mostly absent for
            this reason, so a LEFT JOIN to this table on that kind of question returns mostly
            nulls that mean "never seen in local intel".

          Never present a missing row as evidence about someone's affiliation, and never present a
          present row as current. If the question turns on where someone is NOW and they are not
          in a corporation the capsuleer has roles in, this database cannot tell — and esi_call
          CAN: POST characters/affiliation/ with the ids gives every one of them their current
          corporation and alliance in a single call, and GET characters/{id}/corporationhistory/
          says where anyone went and when. That is the answer to "who is still in the corp";
          do not stop at "the cache cannot say".

          It is deliberate. For reading old intel, the corp somebody was in at the time is the
          useful answer. It is the WRONG source for anything about the present: someone who left
          a corporation last month still reads as a member, and nothing about the row says so.

          Use it to name the corp attached to a historical sighting. Do not use it to decide where
          anyone is now. If the question is about current membership and the corporation is not
          one the capsuleer has roles in, look it up with esi_call rather than reporting a cached
          value as though it were current.

        - Characters holds the capsuleer's OWN authenticated characters only. Do not use it to
          decide who someone else is — most character ids in the database are not in it.

        ## Contracts
        - EsiContracts is the header: IssuerId, AssigneeId, AcceptorId, Type, Status, Price,
          Reward, Collateral, Buyout, and the dates. "Who bought it" is AcceptorId; "who put it
          up" is IssuerId. ForCorporation says the contract belongs to a corporation rather than
          the character.
        - ⚠️ EsiContractItems.IsIncluded splits two completely different things. True is what the
          contract HANDS OVER; false is what it ASKS FOR in return. Both are present in real data.
          Summing without filtering adds what was given to what was demanded.
        - ⚠️ EsiContracts.ItemsPulled says whether the item list was ever retrieved. Some corp
          contracts issued by another corporation return 404 from ESI and their items are simply
          absent. A total over contract items silently under-counts unless those are excluded or
          the shortfall is stated.

        ## Build cost
        - BuildCosts is precomputed per item — total, materials, job cost and build time — and is
          what it costs THIS capsuleer to make the thing. It is not a market price and is usually
          well below one.
        - ⚠️ Bought = true means the item is treated as PURCHASED rather than manufactured, so its
          cost is an acquisition price and not a build. Mixing the two silently compares different
          things.
        - Build cost comes from the capsuleer's own industry setup, so it moves when that changes.
          IndyParks defines which structure builds which category — one park is IsDefault, and it
          is the one build cost and the opportunity tools use. IndyCategoryAssignments holds the
          per-category exceptions.

        ## Things the capsuleer has configured
        These say what they INTEND, which is often the real answer to "what should I do".
        - InvLevel* — target quantities of items they want to hold. Collections contain groups,
          groups contain items, and the group carries a multiplier.
        - MarketLevel* — the same shape, but targets for what should be listed on a market.
        - Worklist* — the industry planner's configuration. WorklistInvRules carries the
          thresholds that decide when something needs making, whether it is a final product, and
          where.
        - CorpStandingProjects — repeating corporation goals the capsuleer defined themselves,
          matched against live ESI corp projects.

        ## Industry jobs
        - ActivityId: 1 manufacturing, 3 time efficiency, 4 material efficiency, 5 copying,
          8 invention, 9 reactions.
        - Status is text: 'active', 'ready', 'delivered', 'cancelled', 'paused'.
        - OwnerType is 'character' or 'corporation', and OwnerId means a different thing for each.
          A query that ignores it mixes personal and corporate work.

        ## Local logs — only this PC, only since it was switched on
        - GameLogEvents and ChatMessages are read from the EVE client's own files on this machine.
          They cover only characters played here, and only since log import was enabled. They
          cannot be backfilled — if the answer is not there, it never will be.
        - ⚠️ GameLogEvents has no Message column. The line's text is in RawText. On SQLite an
          unknown double-quoted name is read as a string literal rather than failing, so querying
          "Message" returns the word Message on every row and looks like data.
        - ⚠️ The client writes an undock line only for NPC stations. Undocking from a player
          structure produces nothing, so movement.undocked is silent for anyone living in a
          Keepstar — absence there is not evidence.
        - For where a character is NOW and what they are flying, CharacterStatuses is better: it
          is polled from ESI, covers every authenticated character, and carries the current ship.

        ## Two general traps
        - ⚠️ Anything keyed by ConfigId, GroupId or OwnerType holds a row PER key. Joining without
          filtering multiplies every SUM by however many exist, and the result looks plausible.
        - ⚠️ A count belongs in SQL. COUNT() and SUM() let the database do the counting; fetching
          rows and counting them is capped by LIMIT and gives the limit back as though it were the
          answer.
        """;
}

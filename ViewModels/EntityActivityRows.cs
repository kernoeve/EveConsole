using Avalonia.Media;
using EveConsole.Localization;
using EveConsole.Services;

namespace EveConsole.ViewModels;

// Rows for the Player Entities viewer's Contracts, Market and Corp Taxes tabs, worded from the
// raw data PlayerActivityService returns. Every link goes by id.

public sealed class EntityContractRowVm(EntityContractData d)
{
    private static EntityNavigator Nav => EntityNavigator.Instance;

    public int            ContractId  => d.Contract.ContractId;
    public DateTimeOffset Issued      => d.Contract.DateIssued;
    public string         IssuedText  => ContractFmt.Date(d.Contract.DateIssued);
    public string         TypeText    => ContractFmt.TypeLabel(d.Contract.Type);
    public string         StatusText  => ContractFmt.EffectiveStatusLabel(d.Contract.Status, d.Contract.DateExpired);
    public IBrush         StatusColor => ContractFmt.EffectiveStatusColor(d.Contract.Status, d.Contract.DateExpired);

    public string RoleText => d.Role switch
    {
        EntityContractRole.Acceptor   => CorpText.EntityContractAccepted,
        EntityContractRole.Assignee   => CorpText.EntityContractAssigned,
        EntityContractRole.IssuerCorp => CorpText.EntityContractByMember,
        _                             => CorpText.EntityContractIssued,
    };

    public string  Contents      => d.Contents.Length > 0 ? d.Contents : $"#{ContractId}";
    public decimal Price         => d.Contract.Price;
    public decimal Reward        => d.Contract.Reward;
    public string  PriceText     => Price  > 0 ? Price.ToString("N0")  : "—";
    public string  RewardText    => Reward > 0 ? Reward.ToString("N0") : "—";

    public string  OtherParty    => d.OtherPartyName;
    public bool    HasOtherParty => d.OtherPartyId > 0 && d.OtherPartyName.Length > 0;
    public string  Location      => d.Location;
    public bool    HasLocationLink => d.LocationId > 0 && d.Location.Length > 0;
    public string  Region        => d.Region;

    /// <summary>Our own copy opens on the Contracts tool's first tab, a public listing on its own.</summary>
    public void OpenContract()
    {
        if (d.IsPublic) Nav.PublicContract(ContractId);
        else            Nav.Contract(ContractId);
    }
    public void OpenOtherParty() => Nav.Entity(d.OtherPartyKind, d.OtherPartyId);
    public void OpenLocation()
    {
        if (d.IsStation) Nav.Entity(EntityKind.Station, d.LocationId);
        else             Nav.Structure(d.LocationId);
    }
}

public sealed class EntityTradeRowVm(EntityTradeData d)
{
    private static EntityNavigator Nav => EntityNavigator.Instance;

    public DateTimeOffset Date      => d.Tx.Date;
    public string  DateText         => ContractFmt.Date(d.Tx.Date);
    public int     TypeId           => d.Tx.TypeId;
    public string  TypeName         => d.TypeName;
    public bool    TheyBought       => d.TheyBought;
    public string  SideText         => d.TheyBought ? CorpText.EntityTradeBought : CorpText.EntityTradeSold;
    public IBrush  SideColor        => d.TheyBought ? Palette.Info : Palette.Good;
    public int     Quantity         => d.Tx.Quantity;
    public string  QuantityText     => d.Tx.Quantity.ToString("N0");
    public decimal UnitPrice        => d.Tx.UnitPrice;
    public string  UnitPriceText    => d.Tx.UnitPrice.ToString("N2");
    public decimal Total            => d.Tx.UnitPrice * d.Tx.Quantity;
    public string  TotalText        => Total.ToString("N0");
    public string  OtherParty       => d.OtherPartyName;
    public bool    HasOtherParty    => d.OtherPartyId > 0 && d.OtherPartyName.Length > 0;
    public string  Location         => d.Location;

    public void OpenItem()       => Nav.Item(d.Tx.TypeId);
    public void OpenOtherParty() => Nav.Entity(d.OtherPartyKind, d.OtherPartyId);
}

public sealed class EntityTaxMonthRowVm(EntityTaxMonth m)
{
    public int     SortKey      => m.Year * 100 + m.Month;
    public string  MonthText    => new DateTime(m.Year, m.Month, 1).ToString("yyyy-MM");
    public string  Corporation  => m.CorpName;
    public decimal Ratting      => m.Ratting;
    public decimal Industry     => m.Industry;
    public decimal Total        => m.Ratting + m.Industry;
    public string  RattingText  => m.Ratting  > 0 ? m.Ratting.ToString("N0")  : "—";
    public string  IndustryText => m.Industry > 0 ? m.Industry.ToString("N0") : "—";
    public string  TotalText    => Total.ToString("N0");

    public void OpenCorporation() => EntityNavigator.Instance.Entity(EntityKind.PlayerCorp, m.CorpId);
}

/// <summary>The panel under the corporation history: one stretch, summed.</summary>
public sealed class EntityStintSummaryVm(EntityHistoryRow stint, EntityStintSummary s)
{
    public string Heading => string.Format(CorpText.StintHeading, stint.Alliance, stint.From, stint.Until);

    public string KillsText     => s.Kills.ToString("N0");
    public string LossesText    => s.Losses.ToString("N0");
    public string RattingText   => s.Ratting  is { } r ? $"{r:N0} ISK" : CorpText.StintNoJournal;
    public string IndustryText  => s.Industry is { } i ? $"{i:N0} ISK" : CorpText.StintNoJournal;
    public string ContractsText => string.Format(CorpText.StintCountIsk, s.Contracts, s.ContractIsk);
    public string TradesText    => string.Format(CorpText.StintCountIsk, s.Trades, s.TradeIsk);
}

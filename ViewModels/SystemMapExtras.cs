using System.Globalization;
using System.Reactive;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// What the map tool knows about a system that the system service does not: its Ansiblex zone
/// (null where nobody holds it), its jump bridges, its Thera and Turnur wormholes, and who is
/// placed there now — hostiles by intel or killmail, and the capsuleer's own characters.
/// </summary>
public sealed record SystemMapExtras(
    SystemZone?                         Zone,
    IReadOnlyList<JumpBridge>           Bridges,
    IReadOnlyList<EveScoutConnection>   Holes,
    SystemHostiles?                     Hostiles,
    IReadOnlyList<OwnPilot>             Own,
    IReadOnlyList<EveScoutStorm>?       Storms = null,
    IReadOnlyList<SovCampaign>?         Campaigns = null);

/// <summary>A jump bridge out of the system on its page: where it goes, its gate here, and who
/// may jump each way, at what zone.</summary>
public sealed class SysBridgeVm
{
    public SysBridgeVm(JumpBridge b, int systemId)
    {
        var here = b.SystemA == systemId;
        OtherSystemId = here ? b.SystemB : b.SystemA;
        OtherLabel    = SdeNames.SolarSystem(OtherSystemId, here ? b.NameB : b.NameA);
        Gate          = b.Gates.FirstOrDefault(g => g.SystemId == systemId)?.Name
                        ?? (b.IsManual ? MapText.BridgeSourceManual : "");
        Access        = BridgeRowVm.AccessText(b);
        Fuel          = b.FuelExpires is { } f
            ? string.Format(MapText.BridgeFuelDays, Math.Max(0, (int)(f - DateTimeOffset.UtcNow).TotalDays),
                            f.ToLocalTime().ToString("d", CultureInfo.CurrentCulture))
            : "";
        Signature     = $"{Gate}|{Access}|{Fuel}";
        OpenCommand   = ReactiveCommand.Create(() => EntityNavigator.Instance.System(OtherSystemId));
    }

    public int    OtherSystemId { get; }
    public string OtherLabel    { get; }
    public string Gate          { get; }
    public string Access        { get; }
    public string Fuel          { get; }
    /// <summary>What a refresh compares.</summary>
    public string Signature     { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
}

/// <summary>A Thera or Turnur wormhole, seen from the system on its page: where it leads, the
/// signature here and on the far side, the largest ship it takes and the time left.</summary>
public sealed class SysHoleVm
{
    public SysHoleVm(EveScoutConnection c, int systemId)
    {
        var atHub = c.HubSystemId == systemId;
        Key       = c.Id;
        FarId     = atHub ? c.OtherSystemId : c.HubSystemId;
        Far       = atHub
            ? SdeNames.SolarSystem(c.OtherSystemId, c.OtherSystemName)
              + (c.OtherRegionName.Length > 0 ? $" ({(c.OtherRegionId is int r ? SdeNames.Region(r, c.OtherRegionName) : c.OtherRegionName)})" : "")
            : SdeNames.SolarSystem(c.HubSystemId, c.HubSystemName);
        HereSig   = atHub ? c.HubSignature   : c.OtherSignature;
        FarSig    = atHub ? c.OtherSignature : c.HubSignature;
        Size      = MapToolViewModel.HoleSize(c.MaxShipSize);
        Left      = MapToolViewModel.HoleLeft(c.ExpiresAt);
        Signature = $"{HereSig}|{FarSig}|{c.MaxShipSize}|{Left}";
        OpenCommand = ReactiveCommand.Create(() => EntityNavigator.Instance.System(FarId));
    }

    public string Key       { get; }
    public int    FarId     { get; }
    public string Far       { get; }
    public string HereSig   { get; }
    public string FarSig    { get; }
    public string Size      { get; }
    public string Left      { get; }
    /// <summary>What a refresh compares: the time left is part of it, so it counts down.</summary>
    public string Signature { get; }
    /// <summary>Thera is in wormhole space, off the map: nothing to open.</summary>
    public bool   CanOpen   => FarId < 31_000_000;
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
}

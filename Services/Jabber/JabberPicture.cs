namespace EveConsole.Services.Jabber;

/// <summary>This account's own picture: set, or taken away, only when the person says so.</summary>
public sealed partial class JabberService
{
    /// <summary>The EVE characters added to this app, to offer their portraits as a picture.</summary>
    public Task<List<(long Id, string Name)>> EveCharactersAsync(CancellationToken ct = default) => OwnPicture.EveCharactersAsync(dbFactory, ct);

    /// <summary>
    /// Sets this account's picture on its server (vCard and published avatar), or takes it away with null. ⚠️ Never done
    /// unasked: the picture there may have been set by another client, or by the server itself.
    /// </summary>
    /// <param name="jpeg">A picture already made ready (<see cref="EveConsole.Services.OwnPicture.Prepare"/>), or null to take it away.</param>
    public async Task SetPictureAsync(JabberSession session, byte[]? jpeg, CancellationToken ct = default)
    {
        if (!ActsHere)
        {
            // Not a secret, and small: the picture goes in the request as it is.
            await AskWorkerAsync(session.Account.Id, "picture", new CommandArgs(Text: jpeg is null ? "" : Convert.ToBase64String(jpeg)), ct);
            return;
        }

        if (session is not { State: JabberState.Connected, Client: { } client })
            throw new InvalidOperationException(Localization.JabberText.PictureNotConnected);

        var rooms = session.Locked(s => s.RoomMap.Values.Where(r => r.Joined).Select(r => (r.Room.RoomJid, r.Nick)).ToList());
        await client.SetPhotoAsync(jpeg, rooms, ct);

        // Shown here at once: the server says nothing back to the account that set it.
        if (avatars is null) return;
        var me = XmppAddress.Parse(session.Account.Address).Bare;
        if (jpeg is null) await avatars.NoteAsync(AvatarServiceName, session.Account.Id, [me, "me"], "", () => Task.FromResult<byte[]?>(null), ct: ct);
        else await avatars.NoteAsync(AvatarServiceName, session.Account.Id, [me, "me"], Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(jpeg)),
            () => Task.FromResult<byte[]?>(jpeg), ct: ct);
    }
}

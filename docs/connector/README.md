# EVE Console connector interface, version 1

A **connector** is a separate program that answers EVE Console's questions about services a
corporation or alliance runs: first, which characters belong to one player. EVE Console defines the
interface; anyone can write a connector for their own group's services. EVE Console itself never talks
to those services.

- **Who runs it:** the EVE Console user, usually on the same PC (on Windows, a tray program in their
  session), or on another device or in a container. A corporation or alliance may instead host one
  connector for its members; how it is set up is up to them.
- **Without one:** EVE Console works exactly as before. Nothing below happens until the user connects.
- **The machine-readable description** is [`openapi.yaml`](openapi.yaml).

## Who trusts whom

**The connector does not trust EVE Console.** EVE Console is a client anyone can run or change, so it is
never the one saying who a character is. The connector learns that from EVE directly, through its own
EVE sign-in: its own EVE application, registered at developers.eveonline.com, and its own callback. A
token from that sign-in is issued to the connector's application, so no other service would accept it,
and EVE Console never sees it.

What EVE Console holds is a **key**, issued by the connector at the end of a sign-in, for the character
that signed in. That key is the only thing it sends afterwards.

## Connecting

1. The user types the connector's address in **Settings → Connector** and chooses **Connect with EVE**.
2. EVE Console calls `GET /v1/info` to confirm it is a connector that speaks version 1.
3. EVE Console calls `POST /v1/auth/start`. The connector starts a sign-in and answers with:
   - `loginUrl`: its own page for this sign-in;
   - `code`: a short code, such as `K7P-4QX`;
   - `loginId` and `pollSecret`: for asking how the sign-in is going;
   - optionally `interval` (seconds between polls, default 2) and `expiresIn` (seconds, default 600,
     at most 900).
4. EVE Console shows the user the **code**, and opens `loginUrl` in their browser.
5. The connector's page shows the **same code** and asks the user to confirm it is the one EVE Console
   is showing. Only after they confirm does it send them to EVE's sign-in. EVE sends them back to the
   connector's callback; the connector checks who signed in and whether that character may connect.
6. Meanwhile EVE Console calls `POST /v1/auth/poll` with `loginId` and `pollSecret` every `interval`
   seconds. The answer is `{"status": "pending"}` until the sign-in ends, then once:
   - `{"status": "done", "key": "…", "character": {"id": …, "name": "…"}}`;
   - `{"status": "refused", "error": "not_allowed"}` (or `eve_token_invalid`, …): signed in, but not
     allowed;
   - `{"status": "declined"}`: the user said the code did not match, or cancelled;
   - `{"status": "expired"}`: not finished in time.
7. Every later call sends `Authorization: Bearer <key>`.

**Why the code.** Anyone can start a sign-in and send someone the link. Without the code, whoever
started it would be handed a key for whoever finished it. The page asks the user to compare codes, so a
sign-in somebody else started is declined instead.

The key belongs to the EVE Console database, not to one PC: on a database shared by several PCs, the
PC holding the background worker lease refreshes every 30 minutes, and any PC can refresh on demand.

## What a connector must do

**Run the sign-in:**

- Register its own EVE application, with a callback at the connector. It needs no scopes to know who
  signed in; ask for scopes only if the connector itself uses them.
- On `/v1/auth/start`: make a new sign-in with an unguessable `loginId`, a `pollSecret` of at least 128
  random bits, and a short `code` that is easy to compare (letters and digits, no look-alikes). Keep
  only a hash of the secret. Limit how many sign-ins can be started, for example per address; answer
  `429` with `too_many_logins` past it.
- On its page: show the code and the connector's name, ask the user to confirm it matches what EVE
  Console shows, and offer a way to decline. Only on confirm, go to EVE's sign-in, with the sign-in's own
  `state`.
- On its callback: check `state`, exchange the code with EVE, and check the token: CCP's signature
  (`https://login.eveonline.com/oauth/jwks`), not expired, issuer `https://login.eveonline.com`, and an
  audience containing **the connector's own client id**. The character is the number in `sub`
  (`CHARACTER:EVE:<id>`); the name is in `name`. Then decide whether the character may connect.
- On `/v1/auth/poll`: answer `404` with `login_unknown` for an unknown sign-in or a wrong secret, and
  `pending` until it ends. Give the final answer **once**, then forget the sign-in. A sign-in no one
  finished expires after `expiresIn`.
- Never put the poll secret or the key in an address: both travel only in request bodies and headers.

**Issue and keep keys:**

- At least 256 random bits, given once. Store only a hash of it.
- Tied to the character id it was issued for.
- Ended by `POST /v1/auth/sign-out`, and revocable by whoever runs the connector.
- May expire after a time unused, set by whoever runs it (90 days is a sensible default). An unknown,
  ended or expired key gets `401`; EVE Console then forgets it and asks the user to connect again.

**Keep checking who may use it.** The connector knows the character behind each key, so it can check
the character's corporation and alliance again, for example through ESI's public
`POST /characters/affiliation/`, or through its own source. A character no longer allowed gets `403`
with `"error": "not_allowed"`; EVE Console keeps what it already has and shows the refusal.

**Transport:**

- A connector on another device must use HTTPS. EVE Console refuses `http://` for anything but this PC,
  for the connector's address and for the sign-in page it is given.
- A connector on the same PC should listen on `127.0.0.1` only. Port **5054** is the suggested default.
- EVE Console does not follow redirects. A redirect is treated as a refusal, so a key never goes
  anywhere but the address the user typed. A connector that moves needs its new address typed in.

## Errors

Every refusal is JSON:

```json
{ "error": "not_allowed", "message": "Only members of the example corporation may connect." }
```

| `error`              | Status | Meaning                                                                 |
|----------------------|--------|-------------------------------------------------------------------------|
| `bad_request`        | 400    | The request is not one this connector understands.                      |
| `not_signed_in`      | 401    | No key, or one the connector does not know (ended, revoked, expired).   |
| `not_allowed`        | 403    | The character may not use this connector, or this part of it.           |
| `not_offered`        | 404    | The connector does not offer this.                                      |
| `login_unknown`      | 404    | `/v1/auth/poll`: no such sign-in, a wrong secret, or one already answered. |
| `too_many_logins`    | 429    | `/v1/auth/start`: too many sign-ins started; try again later.           |
| `eve_unavailable`    | 503    | EVE's sign-in could not be completed or checked just now.               |
| `source_unavailable` | 503    | The service the connector reads from could not be reached just now.     |

In a poll's `refused` answer, `error` is one of these codes, or `eve_token_invalid` when the EVE
sign-in failed the connector's checks.

EVE Console shows its own text for these codes; `message` is shown, in English, only for a code it
does not know.

## Capabilities

`GET /v1/me` lists what the connector offers. Version 1 defines:

| Capability          | Call                        | What EVE Console does with it                   |
|---------------------|-----------------------------|-------------------------------------------------|
| `linked-characters` | `GET /v1/linked-characters` | Keeps which characters are one player's, and which is the main. |

A connector lists only what it offers, and what this character may use. EVE Console ignores names it
does not know, so new capabilities can be added without a new version.

## Linked characters

The answer is the **whole** set of links the connector has. EVE Console replaces what it had before with
it, in one step: a character left out is gone, and an empty `accounts` list empties it. One connector is
connected at a time, so an answer also replaces links from any other `source`. Only current links: moves
and their history stay with the source.

A `404` (`not_offered`) from this call is read as the connector no longer offering linked characters.
EVE Console keeps what it already has until the user disconnects; disconnecting, or connecting to a
different connector, forgets it.

```json
{
  "format": "eveconsole.linked-characters",
  "version": 1,
  "source": "example",
  "generatedAt": "2026-11-08T12:00:00Z",
  "accounts": [
    { "id": 1001, "name": "Example Main",
      "characters": [
        { "id": 90000001, "name": "Example Main", "main": true,
          "corporationId": 98000001, "allianceId": 99000001, "tokenValid": true },
        { "id": 90000002, "name": "Example Alt", "corporationId": 98000002 } ] } ]
}
```

- `source`: a short label (at most 64 characters) for where the links came from.
- `generatedAt`: when the connector read them. EVE Console never stores an answer older than what it
  has for the same source, so return the time of the data, not of the request.
- `accounts[].id`: the source's own account id, stable for as long as the account exists.
- `characters[].main`: at most one per account. `allianceId` 0 or missing means none. `tokenValid` is
  whether the source holds a working EVE token for the character, when it knows.

EVE Console refuses the whole answer, and keeps what it had, if:

- the format or version is wrong, or the source or time is missing;
- an account or character id is missing, zero or negative;
- an account appears twice, or a character appears twice or under two accounts;
- an account has two mains;
- a `corporationId` is negative, or a `corporationId` or `allianceId` is too large for a 32-bit
  number (EVE's corporation and alliance ids always fit).

## Versions

`GET /v1/info` lists the versions a connector speaks. A later version gets its own paths (`/v2/...`),
so a connector can serve several versions at once while EVE Console installs upgrade.

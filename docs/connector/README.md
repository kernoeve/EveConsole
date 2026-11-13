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

## Connecting

1. The user types the connector's address in **Settings → Connector** and chooses **Connect with EVE**.
2. EVE Console calls `GET /v1/info` to confirm it is a connector that speaks version 1.
3. EVE Console opens EVE's sign-in in the browser, using EVE Console's **connector application**. That
   application has **no scopes**: its token proves which character signed in, and can read nothing.
4. EVE Console sends that token once, to `POST /v1/auth/eve`.
5. The connector checks the token (see below), decides whether this character may connect, and answers
   with a **key** for that character.
6. Every later call sends `Authorization: Bearer <key>`. EVE Console keeps no EVE token.

The key belongs to the EVE Console database, not to one PC: on a database shared by several PCs, the
PC holding the background worker lease refreshes every 30 minutes, and any PC can refresh on demand.

## What a connector must do

**Check the EVE token** sent to `/v1/auth/eve`:

- It is a JWT signed by CCP. Check the signature against CCP's keys
  (`https://login.eveonline.com/oauth/jwks`), and that it has not expired.
- The issuer is `https://login.eveonline.com` (older tokens say `login.eveonline.com`).
- The audience contains **EVE Console's connector application client id** and `EVE Online`. Refuse a
  token issued to any other application, including EVE Console's other ones: a token meant for one
  service must not sign anyone in to another.
- The token carries **no scopes**. Refuse one that does: EVE Console never sends a token that can
  read anything.
- The character is the number in `sub` (`CHARACTER:EVE:<id>`); the name is in `name`.
- Use the token once and forget it. Never store or forward it.

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

- A connector on another device must use HTTPS. EVE Console refuses `http://` for anything but this PC.
- A connector on the same PC should listen on `127.0.0.1` only. Port **5054** is the suggested default.
- EVE Console does not follow redirects. A redirect is treated as a refusal, so a key or token never goes
  anywhere but the address the user typed. A connector that moves needs its new address typed in.

## Errors

Every refusal is JSON:

```json
{ "error": "not_allowed", "message": "Only members of the example corporation may connect." }
```

| `error`              | Status | Meaning                                                                 |
|----------------------|--------|-------------------------------------------------------------------------|
| `bad_request`        | 400    | The request is not one this connector understands.                      |
| `eve_token_invalid`  | 401    | The EVE token failed a check above. Only from `/v1/auth/eve`.           |
| `not_signed_in`      | 401    | No key, or one the connector does not know (ended, revoked, expired).   |
| `not_allowed`        | 403    | The character may not use this connector, or this part of it.           |
| `not_offered`        | 404    | The connector does not offer this.                                      |
| `eve_unavailable`    | 503    | CCP's keys could not be read just now.                                  |
| `source_unavailable` | 503    | The service the connector reads from could not be reached just now.     |

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

# EVE Console connector interface, version 1

A **connector** is a separate program that answers EVE Console's questions using what a corporation or
alliance already has: the EVE tokens its members gave its own services, and data those services collect
themselves. EVE Console defines the interface; anyone can write a connector for their own group's
services. EVE Console itself never talks to those services.

A connector serves two kinds of data:

- **ESI data, made with tokens the connector's side holds** (see [ESI data](#esi-data)). EVE Console
  can read a character's or corporation's ESI data without ever holding their token. Read only: there is
  no way to make the connector write anything to ESI.
- **Data the corporation or alliance collects itself:** which characters belong to one player
  ([linked characters](#linked-characters)), [groups](#groups), and
  [fleet participation](#fleet-participation).

- **Who runs it:** the EVE Console user, usually on the same PC (on Windows, a tray program in their
  session), or on another device or in a container. A corporation or alliance may instead host one
  connector for its members; how it is set up is up to them.
- **Without one:** EVE Console works exactly as before. Nothing below happens until the user connects.
- **Each connector offers only what it can.** `GET /v1/me` says what this character may use; EVE Console
  calls nothing else.
- **The machine-readable description** is [`openapi.yaml`](openapi.yaml).

**What EVE Console uses today:** connecting, `/v1/me`'s `capabilities`, and `GET /v1/linked-characters`.
The rest is defined so connectors can offer it; the EVE Console features that read it, and the client
rules that go with them (`offers`, the newer error codes), come later.

**Several connectors.** A user may connect EVE Console to more than one connector, for example when two
of their corporations, or a corporation and an alliance, each run their own. For each connector the user
chooses which of their corporations and alliances it is used for, and which of its capabilities EVE
Console uses from it; for a character, a connector chosen for its corporation comes before one chosen for
its alliance. EVE Console asks a connector nothing about characters or corporations outside what the user
chose for it, and keeps each connector's answers apart. A connector needs to do nothing for this: each
connection is its own sign-in and its own key.

## Who trusts whom

**The connector does not trust EVE Console.** EVE Console is a client anyone can run or change, so it is
never the one saying who a character is. The connector learns that from EVE directly, through its own
EVE sign-in: its own EVE application, registered at developers.eveonline.com, and its own callback. A
token from that sign-in is issued to the connector's application, so no other service would accept it,
and EVE Console never sees it.

What EVE Console holds is a **key**, issued by the connector at the end of a sign-in, for the character
that signed in. That key is the only thing it sends afterwards. Every answer is cut to what **that
character** may see, by the connector's own rules.

**EVE Console does not trust the connector's answers blindly either.** It refuses answers that break the
rules below, and keeps data from a connector apart from the data of the user's own characters.

## Connecting

1. The user adds a connector in **Settings → Connectors**, types its address and chooses **Connect with EVE**.
2. EVE Console calls `GET /v1/info` to confirm it is a connector that speaks version 1.
3. EVE Console calls `POST /v1/auth/start`, optionally with `{"client": "EVE Console"}`, which the
   connector may show on its page. The connector starts a sign-in and answers with:
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
PC holding the background worker lease re-reads `/v1/me` and the linked characters of each connector every 30 minutes,
and any PC can do so on demand.

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
`POST /characters/affiliation`, or through its own source. A character no longer allowed gets `403`
with `"error": "not_allowed"`; EVE Console keeps what it already has and shows the refusal.

**Decide who sees what, on every call:**

1. The key gives the caller's character. Nothing EVE Console sends can change that.
2. The connector works out how the character asked about relates to the caller, from **its own
   source**, never from anything EVE Console sends: on the caller's own account, or someone else.
3. Someone else's data is answered only if the connector's rules allow it for this caller: for example a
   recruiter looking at recruits, or someone checking a buyer's groups before a sale. Who gets such a
   grant is up to whoever runs the connector.
4. For someone else, `not_allowed` is the answer both when the character is unknown and when the caller
   may not see them, so the answer never shows whether a character is known.
5. A corporation not in the caller's offer gets `not_allowed` **before** any token is looked up, so the
   answer never shows whether the connector holds tokens for it.
6. For someone else, leave out `accountId` (in groups and fleet answers) unless the caller may also
   read that account under `linked-characters`: which characters share an account is itself private.
   A group member's `main` is still listed, since a group lists accounts by their main; that names one
   character per account, not its alts.
7. Keep a record of every read whose subject is not the caller's own account, including every
   corporation read: who asked, about whom, what, and when.

**Protect ESI and its tokens:**

- Make only the ESI calls in the lists below, only with `GET`.
- Match the **raw** request path, before any percent-decoding, against those routes. Refuse with
  `bad_request` any path with `%`, a `.` or `..` segment, an empty segment (except one trailing slash,
  see [ESI data](#esi-data)), or an id that is not plain decimal digits fitting its type. Build ESI's
  path from the route and the numbers it parsed, and its query from the listed parameters as parsed;
  never pass on the request's own path or query string as text, or any other header.
- The checks above run **before** the cache is looked at: a cached answer is served only to a caller
  allowed to see that subject.
- Answer from a cache until ESI's `Expires`, so repeated questions cost ESI nothing.
- ESI's error limit is shared by everyone using the same tokens, and every ESI error counts against it.
  So: decide `no_token`, `missing_scope` and `no_role` from what the connector knows of its tokens,
  before calling ESI; refuse a `page` past the last known `X-Pages`; count ESI errors per character
  and stop calling ESI for everyone while its error budget is low.
- Limit calls per key **and** per character (a new sign-in gives a new key); answer `429` with
  `rate_limited` and `Retry-After` past it.

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
| `not_allowed`        | 403    | The character may not use this connector, this part of it, or this subject. |
| `not_offered`        | 404    | The connector does not offer this.                                      |
| `not_found`          | 404    | ESI has no such thing, for example a contract id that does not exist.   |
| `login_unknown`      | 404    | `/v1/auth/poll`: no such sign-in, a wrong secret, or one already answered. |
| `no_token`           | 409    | The connector has no working token for this character or corporation.  |
| `missing_scope`      | 409    | It has a token, but without the scope this data needs.                  |
| `no_role`            | 409    | Corporation data that needs an in-game role no token holder has.        |
| `too_many_logins`    | 429    | `/v1/auth/start`: too many sign-ins started; try again later.           |
| `rate_limited`       | 429    | Too many calls; `Retry-After` says when to try again.                   |
| `esi_error`          | 502    | ESI refused the call for another reason; `message` says what ESI said.  |
| `eve_unavailable`    | 503    | EVE's sign-in or ESI could not be reached or checked just now.          |
| `source_unavailable` | 503    | The service the connector reads from could not be reached just now.     |

EVE Console reads the `error` code, not just the status: a `404` can be `not_offered` or `not_found`.
It shows its own text for these codes; `message` is shown, in English, only for a code it does not
know. In a poll's `refused` answer, `error` is one of these codes, or `eve_token_invalid` when the EVE
sign-in failed the connector's checks.

## Capabilities

`GET /v1/me` says who the key is for, and what this connector offers them:

```json
{
  "character": { "id": 90000001, "name": "Example Main" },
  "capabilities": ["linked-characters", "groups", "character-data", "corporation-data",
                   "fleet-participation"],
  "offers": [
    { "capability": "linked-characters", "subjects": "own" },
    { "capability": "groups", "subjects": "own" },
    { "capability": "groups", "subjects": "others" },
    { "capability": "character-data", "subjects": "own",
      "data": ["assets", "wallet", "wallet/journal", "skills", "location", "ship", "online"] },
    { "capability": "character-data", "subjects": "others", "data": ["online", "location", "ship"] },
    { "capability": "corporation-data", "corporations": [98000001], "data": ["assets", "wallets"] },
    { "capability": "fleet-participation", "subjects": "own" }
  ]
}
```

- `capabilities`: every capability with at least one offer.
- `offers`: what the caller may ask about, per capability:
  - `"subjects": "own"`: characters on the caller's own account, as the connector's source has it.
  - `"subjects": "others"`: other characters, when the connector's rules allow this caller; which ones
    is decided on each call.
  - `"corporations": [...]`: these corporations, for `corporation-data` and for
    `GET /v1/corporations/{corporation_id}/fleet-participation`.
  - `data`: for the ESI capabilities, which of the [listed calls](#esi-data) it answers, by their path.
    A connector lists only calls its tokens can make.
- In an offer, a missing `subjects` means `own`, a missing `corporations` means none, and a missing
  `data` means none of the ESI calls.
- A connector without `offers` is read as offering each listed character capability for `own` only,
  with no ESI calls (its `data` is missing), and no corporation data. A connector offering more than
  `linked-characters` should send `offers`.
- Capability names use only `a`–`z`, `0`–`9` and `-`.

| Capability            | Calls                                                                    |
|-----------------------|--------------------------------------------------------------------------|
| `linked-characters`   | `GET /v1/linked-characters`, `GET /v1/characters/{character_id}/account`           |
| `groups`              | `GET /v1/groups`, `GET /v1/characters/{character_id}/groups`, `GET /v1/groups/{group_id}/members` |
| `character-data`      | `GET /v1/esi/characters/{character_id}/...`                                        |
| `corporation-data`    | `GET /v1/esi/corporations/{corporation_id}/...`                                      |
| `fleet-participation` | `GET /v1/characters/{character_id}/fleets`, `GET /v1/corporations/{corporation_id}/fleet-participation` |

EVE Console ignores capabilities and fields it does not know, so new ones can be added without a new
version. It re-reads `/v1/me` at least every 30 minutes, and after a `not_offered` (other than for a
group id), or a `not_allowed` on a call about the caller's own characters. (A `not_allowed` about
someone else is an ordinary answer.)

**Paging.** A list that can be long is paged the way ESI pages: `?page=1`, `2`, …, with the number of
pages in the `X-Pages` header. Answers that are not paged leave `X-Pages` out.

**Times** are UTC in RFC 3339 form, for example `2026-11-08T12:00:00Z`.

## Linked characters

Which characters belong to one player, and which is the main.

`GET /v1/linked-characters` answers **the caller's own account** only, whatever the offers. Anyone else's
account is read one at a time, with `GET /v1/characters/{character_id}/account` (below), which needs a
`linked-characters` offer with `"subjects": "others"`; each such read is recorded.
EVE Console replaces what it had before with this answer, in one step: a character left out is gone,
and an empty `accounts` list empties it. It replaces only what the same connection gave before; answers
from other connectors are kept apart, whatever `source` they name. Only current links: moves and their
history stay with the source.

A `404` (`not_offered`) from this call is read as the connector no longer offering linked characters.
EVE Console keeps what it already has until the user disconnects; disconnecting or removing that
connector, pointing it at another address, or no longer using it for linked characters (unticking the
function, or choosing no corporation or alliance for it) forgets it; other connectors' links stay.

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

- `source`: a short label (at most 64 characters) for where the links came from. EVE Console shows it,
  but tells connectors apart by the connection, not by this label.
- `generatedAt`: when the connector read them. EVE Console never stores an answer older than what it
  has for the same source, so return the time of the data, not of the request.
- `accounts[].id`: the source's own account id, stable for as long as the account exists.
- `characters[].main`: at most one per account. `allianceId` 0 or missing means none. `tokenValid` is
  whether the source holds a working EVE token for the character, when it knows.

EVE Console refuses the whole answer, and keeps what it had, if:

- the format or version is wrong, or the source, time or `accounts` is missing;
- an account or character id is missing, zero or negative;
- an account appears twice, or a character appears twice or under two accounts;
- an account has two mains;
- a `corporationId` or `allianceId` is negative, or too large for a 32-bit number (EVE's corporation
  and alliance ids always fit).

`GET /v1/characters/{character_id}/account` answers **one** account: the one holding that character,
with all its characters. It is how a recruiter sees all of a recruit's characters. The answer has the
same `format`, `version`, `source` and `generatedAt`, with exactly one entry in `accounts`, and the same
checks apply. EVE Console keeps it apart from the caller's own links: it takes no part in the replace
rule or the `generatedAt` check above.

## Groups

Groups the corporation or alliance keeps for its members, for example "Capital pilots", which a sale of
a supercapital may require, or "On trial".

- `GET /v1/groups`: the groups this connector shows, so the user can pick one in EVE Console.

  ```json
  { "generatedAt": "2026-11-08T12:00:00Z",
    "groups": [ { "id": 12, "name": "Capital pilots" }, { "id": 31, "name": "On trial" } ] }
  ```

- `GET /v1/characters/{character_id}/groups`: the groups of the account holding that character.

  ```json
  { "generatedAt": "2026-11-08T12:00:00Z", "accountId": 1001, "state": "active",
    "groups": [ { "id": 12, "name": "Capital pilots" } ] }
  ```

  For someone else, `accountId` is left out as rule 6 under *Decide who sees what* says. `state` is
  `active`, `deactivating` (the source is about to take the account's groups away, for example because
  its tokens stopped working) or `deactivated` (it has).

- `GET /v1/groups/{group_id}/members` (paged): the accounts in a group, each by its main character.
  This is an `others` call: it is how a recruiter finds everyone on trial.

  ```json
  { "generatedAt": "2026-11-08T12:00:00Z",
    "members": [ { "accountId": 1001, "main": { "id": 90000001, "name": "Example Main" } } ] }
  ```

  An account with no main is listed with `main` left out, and `accountId` is left out as rule 6 under
  *Decide who sees what* says; a member with neither is not listed.

A connector shows only groups its operator chose to show. A group it does not show, or does not have,
is `not_offered`, so the answer never shows whether a group exists; this says nothing about the `groups`
capability as a whole.

## ESI data

`GET /v1/esi/<ESI path>`, where `<ESI path>` is **exactly** the ESI path of one of the calls below, for
example `GET /v1/esi/characters/90000001/wallet/journal?page=2`. The path is written as in ESI's current
specification: no `/latest/` or version prefix, and no trailing slash (a connector may treat one
trailing slash as absent). The connector makes that call to ESI with a token it holds for that
character (or, for a corporation, for a member with the role) and answers with:

- **ESI's own JSON body, unchanged**, so a client can read it with the same code it reads ESI with;
- ESI's `X-Pages`, `Expires` and `Last-Modified` headers.

**Which version of ESI's answer.** EVE Console sends ESI's `X-Compatibility-Date` header, a
`YYYY-MM-DD` date. The connector sends ESI its own copy of that date, and the date is part of its cache
key. A date it does not support, or anything that is not such a date, is `bad_request`. Without the
header, the connector uses its own default date, which is part of the cache key the same way.

**Query parameters** are passed on only where the lists name them; any other is `bad_request`.

**Data about someone else** follows the rules above: `subjects: "others"` in `/v1/me`, decided on each
call, and recorded by the connector.

**Not part of version 1:** anything that writes, and anything ESI answers without a token (public
character and corporation data, killmail bodies, market prices, universe data). EVE Console asks ESI for
those itself.

### Character calls

`/v1/esi/characters/{character_id}/` followed by:

| Path                              | ESI scope                                   | Notes                                     |
|-----------------------------------|---------------------------------------------|-------------------------------------------|
| `assets`                          | `esi-assets.read_assets.v1`                 | Paged                                     |
| `clones`                          | `esi-clones.read_clones.v1`                 |                                           |
| `contacts`                        | `esi-characters.read_contacts.v1`           | Paged                                     |
| `contracts`                       | `esi-contracts.read_character_contracts.v1` | Paged                                     |
| `contracts/{contract_id}/items`   | `esi-contracts.read_character_contracts.v1` |                                           |
| `contracts/{contract_id}/bids`    | `esi-contracts.read_character_contracts.v1` |                                           |
| `implants`                        | `esi-clones.read_implants.v1`               |                                           |
| `industry/jobs`                   | `esi-industry.read_character_jobs.v1`       | `include_completed`                       |
| `killmails/recent`                | `esi-killmails.read_killmails.v1`           | Paged; ids and hashes only                |
| `location`                        | `esi-location.read_location.v1`             |                                           |
| `mail`                            | `esi-mail.read_mail.v1`                     | Headers only; `labels` (1 to 25 label ids, repeated `labels=` as ESI takes them), `last_mail_id`. Mail bodies are not offered. |
| `online`                          | `esi-location.read_online.v1`               |                                           |
| `orders`                          | `esi-markets.read_character_orders.v1`      |                                           |
| `orders/history`                  | `esi-markets.read_character_orders.v1`      | Paged                                     |
| `roles`                           | `esi-characters.read_corporation_roles.v1`  | The character's in-game corporation roles |
| `ship`                            | `esi-location.read_ship_type.v1`            |                                           |
| `skillqueue`                      | `esi-skills.read_skillqueue.v1`             |                                           |
| `skills`                          | `esi-skills.read_skills.v1`                 |                                           |
| `standings`                       | `esi-characters.read_standings.v1`          |                                           |
| `wallet`                          | `esi-wallet.read_character_wallet.v1`       | The balance                               |
| `wallet/journal`                  | `esi-wallet.read_character_wallet.v1`       | Paged                                     |
| `wallet/transactions`             | `esi-wallet.read_character_wallet.v1`       | `from_id`                                 |

### Corporation calls

`/v1/esi/corporations/{corporation_id}/` followed by:

| Path                              | ESI scope                                          | In-game role         | Notes            |
|-----------------------------------|----------------------------------------------------|----------------------|------------------|
| `assets`                          | `esi-assets.read_corporation_assets.v1`            | Director             | Paged            |
| `contracts`                       | `esi-contracts.read_corporation_contracts.v1`      |                      | Paged            |
| `contracts/{contract_id}/items`   | `esi-contracts.read_corporation_contracts.v1`      |                      |                  |
| `contracts/{contract_id}/bids`    | `esi-contracts.read_corporation_contracts.v1`      |                      | Paged            |
| `divisions`                       | `esi-corporations.read_divisions.v1`               | Director             | Division names   |
| `industry/jobs`                   | `esi-industry.read_corporation_jobs.v1`            | Factory Manager      | Paged; `include_completed` |
| `members`                         | `esi-corporations.read_corporation_membership.v1`  |                      |                  |
| `membertracking`                  | `esi-corporations.track_members.v1`                | Director             | Logons, location, ship |
| `orders`                          | `esi-markets.read_corporation_orders.v1`           | Accountant or Trader | Paged            |
| `orders/history`                  | `esi-markets.read_corporation_orders.v1`           | Accountant or Trader | Paged            |
| `roles`                           | `esi-corporations.read_corporation_membership.v1`  | Personnel Manager, or any grantable role | Members' roles |
| `wallets`                         | `esi-wallet.read_corporation_wallets.v1`           | Accountant or Junior Accountant | Balances |
| `wallets/{division}/journal`      | `esi-wallet.read_corporation_wallets.v1`           | Accountant or Junior Accountant | Paged |
| `wallets/{division}/transactions` | `esi-wallet.read_corporation_wallets.v1`           | Accountant or Junior Accountant | `from_id` |

Scopes and roles are as ESI documents them; where ESI's documentation differs, ESI wins.

**When the connector cannot answer:** no token for the character, or for any member of the
corporation, is `no_token`; a token without the scope is `missing_scope`; no token holder with the role
is `no_role`. ESI's own refusals are passed on as `not_found` (ESI's `404`), `rate_limited` (ESI's `420`
or `429`, with `Retry-After`), `eve_unavailable` (ESI's `5xx`), or `esi_error` for anything else.

More calls can be added to these lists later, in the same form, without a new version.

## Fleet participation

Fleets the corporation or alliance recorded itself, and who was in them. This is not ESI data.

- `GET /v1/characters/{character_id}/fleets?from=…&to=…` (paged): the fleets one character was in that
  **started** in that time.

  ```json
  { "generatedAt": "2026-11-08T12:00:00Z",
    "fleets": [
      { "fleet": { "id": 5001, "name": "Example roam", "type": "Strategic",
                   "startedAt": "2026-11-07T19:00:00Z", "endedAt": "2026-11-07T21:10:00Z",
                   "commanderId": 90000005 },
        "intervals": [
          { "from": "2026-11-07T19:05:00Z", "to": "2026-11-07T20:30:00Z",
            "shipTypeId": 11567, "solarSystemId": 30000142, "role": "squad_member" } ] } ] }
  ```

  - `fleet.id` is the source's own fleet id, stable for as long as the source keeps the fleet.
  - `intervals` are the stretches the character was in the fleet; the ship, system and role are what
    they were for that stretch, each left out when the source does not know it. `role` is one of
    `fleet_commander`, `wing_commander`, `squad_commander` or `squad_member`.
  - Only ended fleets are answered.

- `GET /v1/corporations/{corporation_id}/fleet-participation?from=…&to=…`: totals per member of a
  corporation, for the corporations in the caller's `fleet-participation` offer. It counts only ended
  fleets that started between `from` and `to`; `lastFleetAt` is the start of the latest one.

  ```json
  { "generatedAt": "2026-11-08T12:00:00Z", "from": "2026-10-01T00:00:00Z", "to": "2026-11-01T00:00:00Z",
    "members": [ { "characterId": 90000001, "accountId": 1001, "fleets": 14, "seconds": 61200,
                   "lastFleetAt": "2026-10-30T19:00:00Z" } ] }
  ```

  `accountId` is left out when the source does not know the account, or, for someone else, when the
  caller may not read that account under `linked-characters`.

`from` and `to` are required; a connector may refuse a span longer than it keeps, with `bad_request`.

## Versions

`GET /v1/info` lists the versions a connector speaks. Adding a capability, a call to the ESI lists, or a
field is not a new version. A change that breaks a client gets its own paths (`/v2/...`), so a connector
can serve several versions at once while EVE Console installs upgrade.

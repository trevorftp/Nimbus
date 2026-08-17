# ADR 0001: Seamless transfer state machine

Status: accepted. Date: 2026-07-27. Relates to issues #19 and #51.

## Context

The seamless path was half-wired: the source backend sent `NimbusSeamlessPrepare` and
waited for the client's `NimbusSeamlessReady`, but `NimbusSeamlessCommit` was defined and
never sent, so a client had no signal that the transfer it prepared for actually
finished. The open question was who should send the commit, and when.

The structural constraint: the SOURCE backend cannot send the commit. Both transfer
implementations (the visual redirect and the unsafe live splice) end with the client's
connection to the source being torn down, so by the time the transfer has succeeded, the
source has no channel to the client anymore. Only the TARGET backend still talks to the
player.

## Decision

The reservation is the vehicle that carries the handshake identity across the network,
and the target backend closes the loop:

```
source backend          client              proxy               registry        target backend
     |--- Prepare(id) --->|                   |                    |                  |
     |<--- Ready(id) -----|                   |                    |                  |
     |--- TransferIntent(id) ---------------------------------->---|                  |
     |                    |                   |<-- drain intents --|                  |
     |                    |                   |--- mint reservation(uid, id) -------->|
     |                    |<== transfer (redirect or splice) ==|   |                  |
     |                    |                   |                    |                  |
     |                    |                   |     join; consume reservation(uid) -->|
     |                    |<------------------------------------------ Commit(id) ---|
```

- `TransferIntentRequest.ClientTransferId` already carried the id from the source
  backend to the registry.
- The proxy's intent dispatcher now threads it through `RequestTransferAsync` into the
  reservation mint, and `TransferReservation.ClientTransferId` persists it (additive
  JSON field, older peers ignore it).
- When the target backend consumes the reservation on join and finds a non-empty
  `ClientTransferId`, it sends `NimbusSeamlessCommit { TransferId }` to the player from
  the game thread.

`NimbusSeamlessAbort` remains the source's pre-transfer failure signal (prepare
timeout, registry rejection, internal error). After the transfer has started the source
can no longer signal anything; a transfer that dies mid-flight surfaces to the client as
a disconnect, which is also what the vanilla path would do.

### Resolved gap: dispatch failures signal nothing

The client contract is "exactly one of Commit or Abort" only for failures the source can
see synchronously. `PostTransferIntentAsync` confirms the registry *accepted* the intent,
not that the transfer happened. The proxy dispatches it later, and an unknown server id, a
stale or maintenance target, or the dispatcher's ready-wait timeout all just log and
return: `TransferIntentResponse` is fire-and-forget once queued, so nothing reaches back to
the source backend.

That left a real window, after `Ready` and before the redirect or splice began, where the
client sat veiled and received neither packet. Issue #51 closes that window through the
registry control plane:

- The proxy reports each failed seamless dispatch as a `TransferFailed` notice carrying the
  source server id, client transfer id, reason and timestamp.
- The registry keeps notices for two minutes, deduplicates them by source and transfer id,
  and drains them from the matching source backend's next heartbeat. Delivery is destructive
  because the source's in-flight map removes the transfer before it sends the abort.
- The registry heartbeat advertises the proxy's seamless ready-wait timeout. The source puts a
  derived expiry on `NimbusSeamlessPrepare` and keeps a separate post-`Ready` in-flight map
  until the dispatch window closes.
- The source sends `NimbusSeamlessAbort` on a matching notice. Unknown source ids and transfer
  ids are ignored, so a stale or misrouted notice cannot abort another player's transfer.

The notice path covers proxy-side validation, target health checks, the ready gate and the
redirect or splice result. It does not add a reason enum or a reservation status field; the
free-form reason remains operator-visible without changing the existing reservation contract.

## Consequences

- The client mod's contract is now complete: `Prepare` -> freeze/veil, `Ready` -> ack,
  then exactly one of `Commit` (from the NEW server, end the veil) or `Abort` (from the
  OLD server, cancel and stay). A client must accept the commit from a different server
  session than the one that sent the prepare; the `TransferId` is the correlation key.
- Unmodded clients are unaffected: they never register the Nimbus channel, so the
  commit packet is never delivered to anything.
- Reservations gain a correlation field that is empty for plain transfers; no registry
  API version bump is needed (additive field over JSON).
- The commit fires for both seamless implementations, including the default
  redirect-under-veil, which is exactly the case where the client needs to know the
  reconnect it just lived through was the transfer completing.

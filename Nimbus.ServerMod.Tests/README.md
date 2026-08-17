# Nimbus.ServerMod.Tests

Integration scenarios for `Nimbus.ServerMod`, running on a real embedded Vintage Story
server via [Atlas](https://github.com/Pixnop/Atlas). No proxy process and no real
network client: the registry is faked on a loopback HTTP port (`FakeRegistry`) and
players join through Atlas's in-memory dummy client.

## Running

Requirements: .NET 10 SDK and a Vintage Story **1.22.x** install, with `VINTAGE_STORY`
pointing at the folder containing `VintagestoryAPI.dll`:

```sh
VINTAGE_STORY=/path/to/vintagestory dotnet test Nimbus.ServerMod.Tests
```

Note on versions: Nimbus targets VS 1.19+, but Atlas's floor is VS 1.22.0 (it hooks the
1.22 server exit lifecycle). The ServerMod only uses stable server APIs, so running the
suite on 1.22 is representative; earlier game versions stay manual-test territory.

## What is covered

Inbound (reservation gating):

- Valid reservation on join → player admitted, `GetForwardedPlayer` exposes the real
  client IP; the forwarding record is cleared when the player disconnects.
- No reservation + `ReservationRequired=true` → player is kicked.
- No reservation + `ReservationRequired=false` → direct join allowed, no forwarding data.
- The consume call carries all four `X-Nimbus-*` headers and its HMAC verifies against an
  independent reimplementation of the canonical string (so a signing bug cannot
  self-validate).
- Registry unreachable + `ReservationRequired=true`: fails open by default (player
  admitted, so an outage does not lock everyone out) and fails closed when
  `FailClosedWhenRegistryUnreachable=true` (player kicked, so an outage cannot be used to
  bypass the proxy). Both modes are covered.

Outbound (transfer commands):

- `/nimbus send` posts a signed transfer intent with the right identity, target, mode
  and requester; unknown, stale, in-maintenance and same-server targets are rejected
  eagerly, before any registry call.
- `/server` honours `AllowPlayerServerCommand` for player callers and rejects console
  callers through its `RequiresPlayer` precondition.
- Seamless mode without a client ack (the dummy player has no Nimbus client mod) aborts
  on the prepare timeout and never reaches the registry.
- A proxy failure notice delivered on the next heartbeat removes the source transfer from
  its in-flight map and sends the source-side abort packet.

Operations:

- The config-file + `/nimbus reload` path wires an unconfigured server and follows
  registry URL changes (`ReloadRewiringScenarios`).
- Heartbeats are HMAC-signed and carry the configured identity and game version.
- `/nimbus status` and `/nimbus servers` reflect the configured identity and the polled
  snapshot, including health flags.

Out of scope by design (single embedded server, in-memory sockets, no client-side code):
two-server transfers, proxy frame handling, and the seamless client handshake beyond the
timeout path.

## Design notes

- **Operator-style setup** (`NimbusHarness.ConfigureAsync`): scenarios configure the mod
  exactly like an operator does, by writing `nimbus-server.json` into the live data path
  and running `/nimbus reload` (which recreates the registry client since #4). No private
  state is written.
- **Reflection at the assembly boundary**: the game's ModLoader loads a *copy* of the staged
  `Nimbus.ServerMod.dll`, so its types are never identity-equal to compile-time
  references; reading mod state and invoking the narrow ready callback go through reflection.
- **Kick detection**: `ITestPlayer.IsConnected` (Atlas 0.5+) reports the settled state
  after a server-side kick; wait with `Until(() => !player.IsConnected)`.
- **Shared world state**: scenarios in a class share the mod instance, and
  `LastSnapshot` survives reconfiguration (a 404 from `/api/servers` does not clear it).
  Scenarios that assert on an *unknown* transfer target must use a name no other
  scenario ever puts in a snapshot.

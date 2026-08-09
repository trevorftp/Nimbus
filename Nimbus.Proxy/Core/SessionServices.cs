namespace Nimbus.Proxy;

// Everything a ProxySession needs from the proxy around it: the shared sticky table, the registry
// client, the UDP override table, the event surface and the two policy caches. A session reads
// through these but owns none of them; the ProxyListener holds the single instance of each and
// lends the same one to every session it accepts.
//
// The members are one concept, not a grab-bag flattened to shrink a signature. Each is optional
// because a session can run with any of them absent: an embedded-only deployment has no registry,
// the tests wire up only the few services the case under test exercises, and a proxy with bans and
// whitelist off leaves those caches null. So an omitted service is a null member here, which is
// exactly what an omitted constructor argument was before this record existed. The constructor
// stores each one and touches none of them, so grouping them changes no behaviour.
internal sealed record SessionServices(
    StickyRouteTable? Stickies = null,
    IRegistryClient? Registry = null,
    UdpRouteOverrides? UdpOverrides = null,
    EventBus? Events = null,
    BanCache? Bans = null,
    WhitelistCache? Whitelist = null);

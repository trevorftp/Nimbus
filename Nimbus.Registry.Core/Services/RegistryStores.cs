namespace Nimbus.Registry.Services;

// The registry's stores as one argument.
//
// Everything that drives the registry from inside the process needs all of them, and passing
// them one by one had grown into a positional list where swapping two arguments of the same
// shape would compile. Named properties make the call site say which store is which, and adding
// the next store does not lengthen anyone's signature.
//
// Only the stores. The services built over them (ReservationService, ApiTokenService) hold no
// state and are constructed from these, so putting them here would mean choosing between the one
// in the container and the one built by hand for the embedded path that has no container.
public sealed class RegistryStores
{
    public required BackendRegistry Backends { get; init; }
    public required ReservationStore Reservations { get; init; }
    public required TransferIntentStore Intents { get; init; }
    public required TransferFailureStore Failures { get; init; }
    public required BanStore Bans { get; init; }
    public required WhitelistStore Whitelist { get; init; }
    public required ApiTokenStore Tokens { get; init; }
}

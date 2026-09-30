// Raised only when code breaks an invariant that callers were expected to check first
// (a programming error). Expected business failures use Result instead.
namespace Commerce.SharedKernel.Domain;

public sealed class DomainException(string message) : Exception(message);

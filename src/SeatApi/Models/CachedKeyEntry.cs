namespace SeatApi.Models;

/// <summary>
/// Cached idempotency key entry containing the SHA-256 request hash and stored 201 JSON payload.
/// </summary>
public record CachedKeyEntry(string RequestHash, string ResponseJson);

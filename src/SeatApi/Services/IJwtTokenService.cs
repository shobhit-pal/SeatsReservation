namespace SeatApi.Services;

public interface IJwtTokenService
{
    (string Token, int ExpiresInSeconds) Create(string userId, string role);
}

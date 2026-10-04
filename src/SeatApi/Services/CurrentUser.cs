using System.Security.Claims;

namespace SeatApi.Services;

public class CurrentUser
{
    public string UserId { get; }
    public string Role { get; }

    public CurrentUser(HttpContext context)
    {
        UserId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) // "sub" is mapped to NameIdentifier by default if MapInboundClaims is true, but since we set MapInboundClaims=false, it will be "sub"
            ?? context.User.FindFirstValue("sub") 
            ?? string.Empty;
        
        Role = context.User.FindFirstValue("role") ?? string.Empty;
    }
}

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SeatApi.Models;
using SeatApi.Services;

namespace SeatApi.Controllers;

[ApiController]
[Route("[controller]")]
public class AuthController : ControllerBase
{
    private readonly IJwtTokenService _jwtTokenService;

    public AuthController(IJwtTokenService jwtTokenService)
    {
        _jwtTokenService = jwtTokenService;
    }

    [HttpPost("token")]
    [AllowAnonymous]
    public IActionResult Token([FromBody] AuthRequest? request)
    {
        if (request == null)
        {
            return BadRequest(new { error = "validation", message = "Body missing or invalid JSON" });
        }

        if (string.IsNullOrWhiteSpace(request.UserId) || !Regex.IsMatch(request.UserId, @"^[A-Za-z0-9_.-]{1,64}$"))
        {
            return BadRequest(new { error = "validation", message = "user_id must be 1-64 chars of [A-Za-z0-9_.-]" });
        }

        if (request.Role != "user" && request.Role != "admin")
        {
            return BadRequest(new { error = "validation", message = "role must be exactly 'user' or 'admin'" });
        }

        var (token, expiresIn) = _jwtTokenService.Create(request.UserId, request.Role);

        return Ok(new AuthResponse
        {
            Token = token,
            UserId = request.UserId,
            Role = request.Role,
            ExpiresIn = expiresIn
        });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SeatApi.Models;
using SeatApi.Services;

namespace SeatApi.Controllers;

[ApiController]
[Route("shows")]
[Authorize(Policy = "AdminOnly")]
public class ShowsController : ControllerBase
{
    private readonly IShowService _showService;

    public ShowsController(IShowService showService) => _showService = showService;

    [HttpPost]
    public async Task<IActionResult> CreateShow([FromBody] CreateShowRequest? request,
                                                CancellationToken ct)
    {
        // Null body means invalid or missing JSON — model binding returned null
        if (request is null)
            return BadRequest(ApiError.Validation("Body missing or invalid JSON"));

        var result = await _showService.CreateShowAsync(request, ct);

        if (!result.IsSuccess)
            return BadRequest(ApiError.Validation(result.ErrorMessage!));

        return StatusCode(201, result.Value);
    }
}

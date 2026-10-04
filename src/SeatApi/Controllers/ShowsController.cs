using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SeatApi.Models;
using SeatApi.Services;

namespace SeatApi.Controllers;

[ApiController]
[Route("shows")]
public class ShowsController : ControllerBase
{
    private readonly IShowService _showService;
    private readonly IReservationService _reservationService;

    public ShowsController(IShowService showService, IReservationService reservationService)
    {
        _showService = showService;
        _reservationService = reservationService;
    }

    [HttpPost]
    [Authorize(Policy = "AdminOnly")]
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

    [HttpPost("{id}/reserve")]
    [Authorize(Policy = "AnyUser")]
    public async Task<IActionResult> Reserve(
        [FromRoute] string id,
        [FromBody] ReserveRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? headerIdempotencyKey,
        CancellationToken ct)
    {
        var currentUser = new CurrentUser(HttpContext);
        var result = await _reservationService.ReserveAsync(
            id, request, headerIdempotencyKey, currentUser.UserId, ct);

        return result.Outcome switch
        {
            ReserveResult.OutcomeType.Created =>
                StatusCode(201, result.Response),

            ReserveResult.OutcomeType.Replay =>
                StatusCode(201, System.Text.Json.JsonSerializer.Deserialize<ReservationResponse>(
                    result.StoredJson!,
                    new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower })),

            ReserveResult.OutcomeType.Decline =>
                StatusCode(result.StatusCode, ApiError.Response(result.ErrorCode!, result.ErrorMessage!, result.Extra)),

            _ => StatusCode(500)
        };
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SeatApi.Models;
using SeatApi.Services;

namespace SeatApi.Controllers;

[ApiController]
[Route("reservations")]
[Authorize(Policy = "AnyUser")]
public class ReservationsController : ControllerBase
{
    private readonly IReservationService _reservationService;

    public ReservationsController(IReservationService reservationService)
    {
        _reservationService = reservationService;
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(
        [FromRoute] string id,
        CancellationToken ct)
    {
        // Non-UUID reservation ID gives 404 reservation-not-found
        if (!Guid.TryParse(id, out var reservationGuid))
        {
            HttpContext.Items["outcome"] = "not-found";
            return NotFound(ApiError.Response("reservation-not-found", "Reservation not found"));
        }

        var currentUser = new CurrentUser(HttpContext);
        HttpContext.Items["user_id"] = currentUser.UserId;

        var result = await _reservationService.CancelAsync(reservationGuid, currentUser.UserId, ct);

        string outcome = result.Outcome switch
        {
            CancelResult.OutcomeType.Cancelled => result.IsEffective ? "cancelled" : "cancel-noop",
            CancelResult.OutcomeType.NotFound => "not-found",
            _ => "internal"
        };
        HttpContext.Items["outcome"] = outcome;

        if (result.Response != null)
        {
            HttpContext.Items["show_id"] = result.Response.ShowId.ToString();
            HttpContext.Items["seat_count"] = result.Response.Seats?.Count;
        }

        return result.Outcome switch
        {
            CancelResult.OutcomeType.Cancelled =>
                Ok(result.Response),

            CancelResult.OutcomeType.NotFound =>
                NotFound(ApiError.Response("reservation-not-found", result.ErrorMessage ?? "Reservation not found")),

            _ => StatusCode(500)
        };
    }
}

using SeatApi.Models;

namespace SeatApi.Services;

public interface IShowService
{
    Task<ServiceResult<ShowResponse>> CreateShowAsync(CreateShowRequest request,
                                                      CancellationToken ct = default);
}

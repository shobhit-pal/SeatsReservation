using SeatApi.Models;

namespace SeatApi.Repositories;

public interface IShowRepository
{
    Task CreateAsync(Show show, IReadOnlyList<string> seatLabels,
                     CancellationToken ct = default);

    Task<ShowStateResponse?> GetShowStateAsync(Guid showId, bool summaryOnly,
                                               CancellationToken ct = default);
}

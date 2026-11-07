using Microsoft.AspNetCore.Mvc;
using SimpleTasks.Contracts;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Projects;

public class BoardModel(WorkClient work) : TicketListPage(work)
{
    public async Task<IActionResult> OnPostMoveAsync(string ticketKey, TicketStatus status, string? afterKey, string? beforeKey, CancellationToken ct)
    {
        try
        {
            await Work.MoveTicketAsync(ticketKey, status, afterKey, beforeKey, ct);
            return new NoContentResult();
        }
        catch (ApiException exception)
        {
            return new ObjectResult(new { error = exception.Message }) { StatusCode = (int)exception.Status };
        }
    }
}

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
            // Done tickets are locked on the board; reopening is a deliberate change on the ticket page.
            var ticket = await Work.TicketAsync(ticketKey, ct);
            if (ticket.Card.Status == TicketStatus.Done)
            {
                return new ObjectResult(new { error = $"{ticketKey} is done, so it can't be moved on the board. Change its status on the ticket page to reopen it." })
                    { StatusCode = StatusCodes.Status409Conflict };
            }
            await Work.MoveTicketAsync(ticketKey, status, afterKey, beforeKey, ct);
            return new NoContentResult();
        }
        catch (ApiException exception)
        {
            return new ObjectResult(new { error = exception.Message }) { StatusCode = (int)exception.Status };
        }
    }
}

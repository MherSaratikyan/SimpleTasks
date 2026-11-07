using SimpleTasks.Services;

namespace SimpleTasks.Pages;

public class IndexModel(WorkClient work) : PageBase
{
    public List<TicketCard> Tickets { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) => Tickets = await work.MyTicketsAsync(ct);
}

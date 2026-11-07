using SimpleTasks.Services;

namespace SimpleTasks.Pages.Projects;

public class BacklogModel(WorkClient work) : TicketListPage(work);

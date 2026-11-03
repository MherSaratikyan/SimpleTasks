using Microsoft.EntityFrameworkCore;
using SimpleTasks.Models;

namespace SimpleTasks.Data;

public class TaskDbContext : DbContext
{
    public TaskDbContext(DbContextOptions<TaskDbContext> options) : base(options)
    {
    }

    public DbSet<TaskItem> Tasks => Set<TaskItem>();
}

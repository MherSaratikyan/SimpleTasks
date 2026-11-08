namespace SimpleTasks.Contracts;

/// <summary>Marker for messages published between services. The type name is the routing key.</summary>
public interface IIntegrationEvent;

public enum ProjectRole
{
    Viewer = 1,
    Member = 2,
    Admin = 3
}

// Identity
public sealed record UserChanged(
    Guid UserId, string Email, string DisplayName, string? AvatarUrl, string TimeZoneId, bool IsActive) : IIntegrationEvent;

/// <summary>SetupToken is the one-time secret for the invite link; only the email may show it.</summary>
public sealed record InviteCreated(
    Guid InviteId, string Email, string? ProjectName, DateTime ExpiresAt, string? SetupToken) : IIntegrationEvent;

public sealed record PasswordResetRequested(string Email, string Token, DateTime ExpiresAt) : IIntegrationEvent;

public sealed record ProjectMembershipGranted(Guid UserId, Guid ProjectId, ProjectRole Role) : IIntegrationEvent;

// Work
public sealed record ProjectInviteRequested(
    string Email, Guid ProjectId, string ProjectName, ProjectRole Role, Guid InvitedBy) : IIntegrationEvent;

/// <summary>Full snapshot of what other services track about a ticket. Older versions are ignored.</summary>
public sealed record TicketChanged(
    Guid TicketId, long Version, string Key, string Title, Guid? AssigneeId,
    DateTime? DueAt, bool IsDone, bool IsDeleted) : IIntegrationEvent;

public sealed record TicketAssigned(
    Guid TicketId, string Key, string Title, Guid AssigneeId, Guid ActorId) : IIntegrationEvent;

public sealed record TicketReadyForReview(
    Guid TicketId, string Key, string Title, Guid QaOwnerId, Guid ActorId) : IIntegrationEvent;

/// <summary>A project and all its tickets were permanently deleted.</summary>
public sealed record ProjectDeleted(Guid ProjectId, string ProjectKey, IReadOnlyList<Guid> TicketIds) : IIntegrationEvent;

public sealed record CommentAdded(
    Guid TicketId, string Key, string Title, Guid ActorId, string ActorName,
    IReadOnlyList<Guid> Recipients) : IIntegrationEvent;

public sealed record EstimateChanged(
    Guid TicketId, string Key, string Title, int? OldMinutes, int? NewMinutes, Guid ActorId,
    IReadOnlyList<Guid> Recipients) : IIntegrationEvent;

public enum EstimateAlertLevel
{
    None = 0,
    Warning = 1,
    Exceeded = 2
}

public sealed record EstimateThresholdCrossed(
    Guid TicketId, string Key, string Title, EstimateAlertLevel Level, int LoggedMinutes, int EstimateMinutes,
    IReadOnlyList<Guid> Recipients) : IIntegrationEvent;

public enum TicketStatus { ToDo = 0, InProgress = 1, InReview = 2, Done = 3 }
public enum TicketType { Task = 0, Bug = 1, Story = 2 }
public enum TicketPriority { Lowest = 0, Low = 1, Medium = 2, High = 3, Highest = 4 }

public enum NotificationType
{
    Assigned = 0,
    Comment = 1,
    EstimateChanged = 2,
    EstimateAlert = 3,
    DueReminder = 4,
    CustomReminder = 5,
    ReviewRequested = 6
}

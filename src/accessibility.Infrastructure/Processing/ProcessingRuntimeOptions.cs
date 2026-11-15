namespace Accessibility.Infrastructure.Processing;

public sealed class ProcessingRuntimeOptions
{
    public const string SectionName = "Processing";

    public bool WorkerEnabled { get; init; } = true;

    public bool DashboardEnabled { get; init; } = true;

    public int WorkerCount { get; init; } = 1;

    public int WorkspaceMaximumAgeMinutes { get; init; } = 240;
}

namespace Module.Camoprof.SharedLogic;

internal sealed record ProfileEntry(
    string ProfileId,
    string DisplayName,
    string? Email,
    bool IsLinked);

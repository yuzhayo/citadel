namespace Module.Camoprof.Providers.Google;

internal sealed record GoogleAccountRecord(
    string ProfileId,
    string Email,
    string Provider,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

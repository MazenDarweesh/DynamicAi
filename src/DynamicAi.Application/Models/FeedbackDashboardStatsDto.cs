namespace DynamicAi.Application.Models;

/// <summary>
/// Dashboard statistics DTO for Golden Dataset, operational health, and column synonyms.
/// </summary>
public sealed class FeedbackDashboardStatsDto
{
    public DatasetStatsDto Dataset { get; init; } = new();
    public OperationalHealthStatsDto Health { get; init; } = new();
    public SynonymStatsDto Synonyms { get; init; } = new();
}

public sealed class DatasetStatsDto
{
    public int Verified { get; init; }
    public int Total { get; init; }
    public int PendingReview { get; init; }
    public int TargetForFineTuning { get; init; } = 500;
    public double Progress { get; init; }
}

public sealed class OperationalHealthStatsDto
{
    public string Period { get; init; } = string.Empty;
    public int TotalQueries { get; init; }
    public double SuccessRate { get; init; }
    public double ErrorRate { get; init; }
    public int SuccessfulQueries { get; init; }
    public int FailedQueries { get; init; }
    public int Likes { get; init; }
    public int Dislikes { get; init; }
    public double LikeRatio { get; init; }
    public int SelfHealingCount { get; init; }
    public double SelfHealingRate { get; init; }
    public int FallbackCount { get; init; }
    public double FallbackRate { get; init; }
}

public sealed class SynonymStatsDto
{
    public int Pending { get; init; }
    public int Approved { get; init; }
}

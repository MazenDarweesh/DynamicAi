using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Services;
using DynamicAi.Domain.Entities;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Unit.Services;

public class FeedbackServiceOptimizationTests
{
    private readonly ITrainingExampleRepository _trainingRepo = A.Fake<ITrainingExampleRepository>();
    private readonly IQueryLogRepository _queryLogRepo = A.Fake<IQueryLogRepository>();
    private readonly IColumnSynonymRepository _synonymRepo = A.Fake<IColumnSynonymRepository>();
    private readonly IQueryCacheService _queryCacheService = A.Fake<IQueryCacheService>();
    private readonly IConversationContextService _contextService = A.Fake<IConversationContextService>();
    private readonly ILogger<FeedbackService> _logger = A.Fake<ILogger<FeedbackService>>();

    private FeedbackService CreateSut() =>
        new(
            _trainingRepo,
            _queryLogRepo,
            _synonymRepo,
            _queryCacheService,
            _contextService,
            _logger);

    [Fact]
    public async Task RecordPositiveFeedbackAsync_WhenExampleAlreadyExists_SkipsAddAsyncAndUpdatesStatusToLike()
    {
        // Arrange
        var queryLogId = Guid.NewGuid();
        var queryLog = new QueryLog
        {
            Id = queryLogId,
            RawQuestion = "إجمالي المبيعات اليوم",
            GeneratedSql = "SELECT SUM(Total) FROM SalesView",
            SchemaContext = "{\"SalesView\":[\"Total\"]}",
            CompanyId = 7,
            PageId = 244
        };

        A.CallTo(() => _queryLogRepo.GetByIdAsync(queryLogId, A<CancellationToken>._))
            .Returns(queryLog);
        A.CallTo(() => _trainingRepo.ExistsByQueryLogIdAsync(queryLogId, A<CancellationToken>._))
            .Returns(true);
        A.CallTo(() => _trainingRepo.GetVerifiedCountAsync(A<CancellationToken>._))
            .Returns(12);

        var sut = CreateSut();

        // Act
        var result = await sut.RecordPositiveFeedbackAsync(queryLogId);

        // Assert
        result.Should().BeTrue();
        A.CallTo(() => _trainingRepo.AddAsync(A<TrainingExample>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _queryLogRepo.UpdateFeedbackStatusAsync(queryLogId, "Like", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RecordPositiveFeedbackAsync_WhenExampleDoesNotExist_AddsExampleAndUpdatesStatusToLike()
    {
        // Arrange
        var queryLogId = Guid.NewGuid();
        var queryLog = new QueryLog
        {
            Id = queryLogId,
            RawQuestion = "عدد العقود النشطة",
            GeneratedSql = "SELECT COUNT(*) FROM ContractViewWithPaid",
            SchemaContext = "{\"ContractViewWithPaid\":[\"ContractID\"]}",
            CompanyId = 5,
            PageId = 101
        };

        A.CallTo(() => _queryLogRepo.GetByIdAsync(queryLogId, A<CancellationToken>._))
            .Returns(queryLog);
        A.CallTo(() => _trainingRepo.ExistsByQueryLogIdAsync(queryLogId, A<CancellationToken>._))
            .Returns(false);
        A.CallTo(() => _trainingRepo.GetVerifiedCountAsync(A<CancellationToken>._))
            .Returns(13);

        var sut = CreateSut();

        // Act
        var result = await sut.RecordPositiveFeedbackAsync(queryLogId, tenantId: 9);

        // Assert
        result.Should().BeTrue();
        A.CallTo(() => _trainingRepo.AddAsync(
                A<TrainingExample>.That.Matches(e =>
                    e.OriginalQueryLogId == queryLogId &&
                    e.NaturalQuery == queryLog.RawQuestion &&
                    e.GeneratedSql == queryLog.GeneratedSql &&
                    e.SchemaContext == queryLog.SchemaContext &&
                    e.FeedbackSource == "UserLike" &&
                    e.TenantId == 9 &&
                    e.PageId == 101 &&
                    !e.IsVerified),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _queryLogRepo.UpdateFeedbackStatusAsync(queryLogId, "Like", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }
}

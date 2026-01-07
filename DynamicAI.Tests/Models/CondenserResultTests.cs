using DynamicAi.Application.Models;
using FluentAssertions;
using Xunit;

namespace DynamicAI.Tests.Models;

/// <summary>
/// Unit tests for CondenserResult model and its factory methods.
/// </summary>
public class CondenserResultTests
{
    #region Factory Method Tests

    [Fact]
    public void AsNew_SetsCorrectActionType()
    {
        // Arrange & Act
        var result = CondenserResult.AsNew("Test question");

        // Assert
        result.ActionType.Should().Be(QueryActionType.New);
        result.CondensedQuestion.Should().Be("Test question");
    }

    [Fact]
    public void AsWrapPrevious_SetsCorrectActionType()
    {
        // Arrange & Act
        var result = CondenserResult.AsWrapPrevious("Wrapped question");

        // Assert
        result.ActionType.Should().Be(QueryActionType.WrapPrevious);
        result.CondensedQuestion.Should().Be("Wrapped question");
    }

    [Fact]
    public void AsRegenerate_SetsCorrectActionType()
    {
        // Arrange & Act
        var result = CondenserResult.AsRegenerate("Regenerated question");

        // Assert
        result.ActionType.Should().Be(QueryActionType.Regenerate);
        result.CondensedQuestion.Should().Be("Regenerated question");
    }

    #endregion

    #region Property Tests

    [Fact]
    public void DefaultConstructor_SetsNewAsDefault()
    {
        // Arrange & Act
        var result = new CondenserResult();

        // Assert
        result.ActionType.Should().Be(QueryActionType.New);
        result.CondensedQuestion.Should().BeEmpty();
    }

    [Fact]
    public void CondensedQuestion_CanBeSetManually()
    {
        // Arrange
        var result = new CondenserResult
        {
            CondensedQuestion = "Manual question",
            ActionType = QueryActionType.WrapPrevious
        };

        // Assert
        result.CondensedQuestion.Should().Be("Manual question");
        result.ActionType.Should().Be(QueryActionType.WrapPrevious);
    }

    #endregion

    #region QueryActionType Enum Tests

    [Fact]
    public void QueryActionType_HasCorrectValues()
    {
        // Assert enum values exist
        QueryActionType.New.Should().BeDefined();
        QueryActionType.WrapPrevious.Should().BeDefined();
        QueryActionType.Regenerate.Should().BeDefined();
    }

    [Fact]
    public void QueryActionType_New_IsZero()
    {
        // Arrange & Assert - New should be 0 (default for fresh queries)
        ((int)QueryActionType.New).Should().Be(0);
    }

    #endregion
}

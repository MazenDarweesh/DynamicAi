// =============================================================================
// FILE: CondenserResultTests.cs
// PURPOSE: Unit tests for CondenserResult model and QueryActionType enum
//
// TESTS COVERAGE:
//   1. Factory Method Tests:
//      - AsNew() creates result with ActionType.New
//      - AsWrapPrevious() creates result with ActionType.WrapPrevious
//      - AsRegenerate() creates result with ActionType.Regenerate
//
//   2. Property Tests:
//      - Default constructor sets ActionType.New + empty string
//      - Properties can be set via object initializer
//
//   3. Enum Tests:
//      - All three enum values are defined
//      - New = 0 (important for default behavior)
//
// TESTS:
//   - CondenserResult.cs → The model being tested
// =============================================================================

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

    // TEST: AsNew() factory creates result with ActionType=New
    [Fact]
    public void AsNew_SetsCorrectActionType()
    {
        // Arrange & Act
        var result = CondenserResult.AsNew("Test question");

        // Assert
        result.ActionType.Should().Be(QueryActionType.New);
        result.CondensedQuestion.Should().Be("Test question");
    }

    // TEST: AsWrapPrevious() factory creates result with ActionType=WrapPrevious
    [Fact]
    public void AsWrapPrevious_SetsCorrectActionType()
    {
        // Arrange & Act
        var result = CondenserResult.AsWrapPrevious("Wrapped question");

        // Assert
        result.ActionType.Should().Be(QueryActionType.WrapPrevious);
        result.CondensedQuestion.Should().Be("Wrapped question");
    }

    // TEST: AsRegenerate() factory creates result with ActionType=Regenerate
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

    // TEST: Default constructor initializes with ActionType=New and empty string
    [Fact]
    public void DefaultConstructor_SetsNewAsDefault()
    {
        // Arrange & Act
        var result = new CondenserResult();

        // Assert
        result.ActionType.Should().Be(QueryActionType.New);
        result.CondensedQuestion.Should().BeEmpty();
    }

    // TEST: Properties can be set manually via object initializer
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

    // TEST: All three enum values (New, WrapPrevious, Regenerate) are defined
    [Fact]
    public void QueryActionType_HasCorrectValues()
    {
        // Assert enum values exist
        QueryActionType.New.Should().BeDefined();
        QueryActionType.WrapPrevious.Should().BeDefined();
        QueryActionType.Regenerate.Should().BeDefined();
    }

    // TEST: Enum value New=0 (important for default behavior)
    [Fact]
    public void QueryActionType_New_IsZero()
    {
        // Arrange & Assert - New should be 0 (default for fresh queries)
        ((int)QueryActionType.New).Should().Be(0);
    }

    #endregion
}

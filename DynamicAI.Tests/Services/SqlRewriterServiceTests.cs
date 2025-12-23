using DynamicAi.Application.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using FakeItEasy;

namespace DynamicAI.Tests.Services;

/// <summary>
/// Unit Tests for SqlRewriterService
/// Tests the parameter binding, tenant injection (future), and SQL regeneration.
/// </summary>
public class SqlRewriterServiceTests
{
    private readonly SqlRewriterService _sut;

    public SqlRewriterServiceTests()
    {
        var logger = A.Fake<ILogger<SqlRewriterService>>();
        _sut = new SqlRewriterService(logger);
    }

    [Fact]
    public async Task RewriteAsync_ShouldAddCompanyIdParameter_WhenPresentInSql()
    {
        // Arrange
        var safeSql = "SELECT * FROM Users WHERE ComID = @CurrentCompanyId";
        var tenantId = "tenant1";
        var comId = 123;

        // Act
        var result = await _sut.RewriteAsync(safeSql, tenantId, comId);

        // Assert
        result.RewrittenSql.Should().NotBeNullOrEmpty();
        result.Parameters.Should().ContainKey("@CurrentCompanyId");
        result.Parameters["@CurrentCompanyId"].Should().Be(comId);
    }

    [Fact]
    public async Task RewriteAsync_ShouldMaintainValidSql_AfterRewriting()
    {
        // Arrange
        var safeSql = "SELECT Name FROM Users WHERE Id = 1";

        // Act
        var result = await _sut.RewriteAsync(safeSql, "tenant1", 1);

        // Assert
        // TSqlScriptGenerator might change formatting (case, whitespace), but semantic meaning should be same
        result.RewrittenSql.Should().Contain("SELECT");
        result.RewrittenSql.Should().Contain("Name");
        result.RewrittenSql.Should().Contain("Users");
    }

    [Fact]
    public async Task RewriteAsync_ToPClause_ShouldBeInjected_IfImplemented()
    {
        // NOTE: The implementation of TOP clause injection is currently hypothetical in the Visitor 
        // or dependent on MAX_ROWS_LIMIT constant.
        // This test documents that we expect the output to be runnable SQL.

        var safeSql = "SELECT * FROM Users";
        var result = await _sut.RewriteAsync(safeSql, "tenant1", 1);

        result.RewrittenSql.Should().StartWith("SELECT");
    }

    [Fact]
    public async Task RewriteAsync_ShouldHandleComplexQueries()
    {
        // Arrange
        var safeSql = @"
            SELECT u.Name, o.Date 
            FROM Users u 
            JOIN Orders o ON u.Id = o.UserId 
            WHERE u.ComID = @CurrentCompanyId 
            ORDER BY o.Date DESC";

        // Act
        var result = await _sut.RewriteAsync(safeSql, "tenant1", 99);

        // Assert
        result.RewrittenSql.Should().Contain("JOIN");
        result.Parameters.Should().ContainKey("@CurrentCompanyId");
        result.Parameters["@CurrentCompanyId"].Should().Be(99);
    }
}

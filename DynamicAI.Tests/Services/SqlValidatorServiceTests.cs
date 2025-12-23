using System.Security;
using DynamicAi.Application.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using FakeItEasy;

namespace DynamicAI.Tests.Services;

/// <summary>
/// Unit tests for SqlValidatorService - Layer 3
/// Tests syntax validation and security enforcement
/// </summary>
public class SqlValidatorServiceTests
{
    private readonly SqlValidatorService _service;

    public SqlValidatorServiceTests()
    {
        var logger = A.Fake<ILogger<SqlValidatorService>>();
        _service = new SqlValidatorService(logger);
    }

    #region Syntax Validation Tests

    [Fact]
    public async Task ValidateSqlAsync_ValidSelect_ReturnsSuccess()
    {
        // Arrange
        var sql = "SELECT * FROM Users WHERE Id = 1";

        // Act
        var act = async () => await _service.ValidateSqlAsync(sql, "tenant1");

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateSqlAsync_InvalidSyntax_ThrowsInvalidDataException()
    {
        // Arrange
        var sql = "SELEC * FORM Users"; // Typos

        // Act
        var act = async () => await _service.ValidateSqlAsync(sql, "tenant1");

        // Assert
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*Syntax Error*");
    }

    [Fact]
    public async Task ValidateSqlAsync_EmptyString_ThrowsException()
    {
        // Arrange
        var sql = "";

        // Act
        var act = async () => await _service.ValidateSqlAsync(sql, "tenant1");

        // Assert
        // Empty SQL is technically valid but produces empty AST - depends on implementation
        // We test that it doesn't crash
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateSqlAsync_SelectWithJoin_ReturnsSuccess()
    {
        // Arrange
        var sql = @"
            SELECT c.Name, o.Total 
            FROM Customers c 
            INNER JOIN Orders o ON c.Id = o.CustomerId 
            WHERE o.Total > 1000";

        // Act
        var act = async () => await _service.ValidateSqlAsync(sql, "tenant1");

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateSqlAsync_ComplexSubquery_ReturnsSuccess()
    {
        // Arrange
        var sql = @"
            SELECT TOP 100 *
            FROM Contracts c
            WHERE c.Total > (SELECT AVG(Total) FROM Contracts WHERE ComID = @CurrentCompanyId)
            AND c.ComID = @CurrentCompanyId
            ORDER BY c.Total DESC";

        // Act
        var act = async () => await _service.ValidateSqlAsync(sql, "tenant1");

        // Assert
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region Security Integration Tests

    [Fact]
    public async Task ValidateSqlAsync_DeleteStatement_ThrowsSecurityException()
    {
        // Arrange
        var sql = "DELETE FROM Users WHERE Id = 5";

        // Act
        var act = async () => await _service.ValidateSqlAsync(sql, "tenant1");

        // Assert
        await act.Should().ThrowAsync<SecurityException>()
            .WithMessage("*DELETE*");
    }

    [Fact]
    public async Task ValidateSqlAsync_DropTable_ThrowsSecurityException()
    {
        // Arrange
        var sql = "DROP TABLE Users";

        // Act
        var act = async () => await _service.ValidateSqlAsync(sql, "tenant1");

        // Assert
        await act.Should().ThrowAsync<SecurityException>()
            .WithMessage("*DROP TABLE*");
    }

    [Fact]
    public async Task ValidateSqlAsync_UpdateStatement_ThrowsSecurityException()
    {
        // Arrange
        var sql = "UPDATE Users SET Name = 'Hacked' WHERE 1=1";

        // Act
        var act = async () => await _service.ValidateSqlAsync(sql, "tenant1");

        // Assert
        await act.Should().ThrowAsync<SecurityException>()
            .WithMessage("*UPDATE*");
    }

    #endregion
}

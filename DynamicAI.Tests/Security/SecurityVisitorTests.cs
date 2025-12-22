using System.Security;
using DynamicAi.Application.Security;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DynamicAI.Tests.Security;

/// <summary>
/// Unit tests for SecurityVisitor - The core security layer
/// Tests DDL/DML blocking and blacklist enforcement
/// </summary>
public class SecurityVisitorTests
{
    private readonly SecurityVisitor _visitor;
    private readonly TSql160Parser _parser;

    public SecurityVisitorTests()
    {
        _visitor = new SecurityVisitor();
        _parser = new TSql160Parser(false);
    }

    private TSqlFragment ParseSql(string sql)
    {
        using var reader = new StringReader(sql);
        var fragment = _parser.Parse(reader, out var errors);
        errors.Should().BeEmpty($"SQL should be valid: {sql}");
        return fragment;
    }

    #region DDL Blocking Tests

    [Fact]
    public void Visit_DeleteStatement_ThrowsSecurityException()
    {
        // Arrange
        var sql = "DELETE FROM Users WHERE Id = 5";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().Throw<SecurityException>()
            .WithMessage("*DELETE*");
    }

    [Fact]
    public void Visit_UpdateStatement_ThrowsSecurityException()
    {
        // Arrange
        var sql = "UPDATE Users SET Name = 'Test' WHERE Id = 1";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().Throw<SecurityException>()
            .WithMessage("*UPDATE*");
    }

    [Fact]
    public void Visit_InsertStatement_ThrowsSecurityException()
    {
        // Arrange
        var sql = "INSERT INTO Users (Name) VALUES ('Test')";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().Throw<SecurityException>()
            .WithMessage("*INSERT*");
    }

    [Fact]
    public void Visit_DropTableStatement_ThrowsSecurityException()
    {
        // Arrange
        var sql = "DROP TABLE Users";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().Throw<SecurityException>()
            .WithMessage("*DROP TABLE*");
    }

    [Fact]
    public void Visit_TruncateStatement_ThrowsSecurityException()
    {
        // Arrange
        var sql = "TRUNCATE TABLE Users";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().Throw<SecurityException>()
            .WithMessage("*TRUNCATE*");
    }

    [Fact]
    public void Visit_AlterTableStatement_ThrowsSecurityException()
    {
        // Arrange
        var sql = "ALTER TABLE Users ADD Email VARCHAR(100)";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().Throw<SecurityException>()
            .WithMessage("*ALTER TABLE*");
    }

    [Fact]
    public void Visit_CreateTableStatement_ThrowsSecurityException()
    {
        // Arrange
        var sql = "CREATE TABLE Hackers (Id INT)";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().Throw<SecurityException>()
            .WithMessage("*CREATE TABLE*");
    }

    #endregion

    #region Permission Blocking Tests

    [Fact]
    public void Visit_GrantStatement_ThrowsSecurityException()
    {
        // Arrange
        var sql = "GRANT SELECT ON Users TO PublicUser";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().Throw<SecurityException>()
            .WithMessage("*GRANT*");
    }

    [Fact]
    public void Visit_DenyStatement_ThrowsSecurityException()
    {
        // Arrange
        var sql = "DENY SELECT ON Users TO PublicUser";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().Throw<SecurityException>()
            .WithMessage("*DENY*");
    }

    [Fact]
    public void Visit_RevokeStatement_ThrowsSecurityException()
    {
        // Arrange
        var sql = "REVOKE SELECT ON Users FROM PublicUser";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().Throw<SecurityException>()
            .WithMessage("*REVOKE*");
    }

    #endregion

    #region Valid SELECT Tests

    [Fact]
    public void Visit_SimpleSelect_DoesNotThrow()
    {
        // Arrange
        var sql = "SELECT * FROM Users WHERE Id = 1";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Visit_SelectWithJoin_DoesNotThrow()
    {
        // Arrange
        var sql = @"
            SELECT u.Name, o.Total 
            FROM Users u 
            INNER JOIN Orders o ON u.Id = o.UserId 
            WHERE o.Total > 100";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Visit_SelectWithSubquery_DoesNotThrow()
    {
        // Arrange
        var sql = @"
            SELECT * FROM Users 
            WHERE Id IN (SELECT UserId FROM Orders WHERE Total > 1000)";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Visit_SelectWithAggregation_DoesNotThrow()
    {
        // Arrange
        var sql = "SELECT COUNT(*), SUM(Total) FROM Orders GROUP BY UserId";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Visit_ComplexSelectWithCTE_DoesNotThrow()
    {
        // Arrange - Even though CTEs are discouraged, they shouldn't throw security exception
        var sql = @"
            WITH OrderTotals AS (
                SELECT UserId, SUM(Total) as TotalAmount
                FROM Orders
                GROUP BY UserId
            )
            SELECT u.Name, ot.TotalAmount
            FROM Users u
            JOIN OrderTotals ot ON u.Id = ot.UserId";
        var fragment = ParseSql(sql);

        // Act
        var act = () => fragment.Accept(_visitor);

        // Assert
        act.Should().NotThrow();
    }

    #endregion
}

using System.Security;
using DynamicAi.Application.Interfaces;
using DynamicAi.Application.Security;
using DynamicAi.Application.Services;
using DynamicAi.Application.Services.SqlEngine;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DynamicAI.Tests.Security;

/// <summary>
/// Red-team security tests verifying strict read-only AST enforcement across
/// SecurityVisitor, SqlValidatorService, and SqlGuardPipeline.
/// </summary>
public class SecurityVisitorRedTeamTests
{
    private readonly TSql160Parser _parser;
    private readonly SqlValidatorService _validator;
    private readonly SqlGuardPipeline _pipeline;

    public SecurityVisitorRedTeamTests()
    {
        _parser = new TSql160Parser(false);
        _validator = new SqlValidatorService(A.Fake<ILogger<SqlValidatorService>>());

        var schemaProvider = A.Fake<IDbSchemaProvider>();
        A.CallTo(() => schemaProvider.GetSchemaMapAsync(A<CancellationToken>._, A<bool>._))
            .Returns(Task.FromResult(CreateSampleSchema()));

        _pipeline = new SqlGuardPipeline(schemaProvider, A.Fake<ILogger<SqlGuardPipeline>>());
    }

    private static Dictionary<string, HashSet<string>> CreateSampleSchema() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ContractViewWithPaid"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "ContractID", "ContractDate", "TotalOrder", "ComID", "CustomerAccountID"
            },
            ["CustomerAccountView"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "CustomerAccountID", "CustomerAccountName", "ComID"
            }
        };

    [Theory]
    [InlineData("EXEC xp_cmdshell 'whoami'")]
    [InlineData("EXEC sp_help")]
    [InlineData("SELECT * INTO Evil FROM ContractViewWithPaid")]
    [InlineData("WAITFOR DELAY '0:0:5'")]
    [InlineData("SELECT 1; DROP TABLE X")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT 1\nGO\nSELECT 2")]
    [InlineData("DBCC CHECKDB")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=.;Trusted_Connection=yes;', 'SELECT 1')")]
    [InlineData("BULK INSERT ContractViewWithPaid FROM 'C:\\evil.csv'")]
    [InlineData("DECLARE @x INT")]
    [InlineData("SET @x = 1")]
    [InlineData("SHUTDOWN")]
    [InlineData("KILL 55")]
    public void SecurityVisitor_RedTeamPayloads_ThrowSecurityException(string maliciousSql)
    {
        using var reader = new StringReader(maliciousSql);
        var fragment = _parser.Parse(reader, out var errors);
        errors.Should().BeEmpty($"payload must parse as T-SQL so AST security layer is tested: {maliciousSql}");

        var act = () => SecurityVisitor.ValidateReadOnlyScript(fragment);

        act.Should().Throw<SecurityException>();
    }

    [Theory]
    [InlineData("EXEC xp_cmdshell 'whoami'")]
    [InlineData("EXEC sp_help")]
    [InlineData("SELECT * INTO Evil FROM ContractViewWithPaid")]
    [InlineData("WAITFOR DELAY '0:0:5'")]
    [InlineData("SELECT 1; DROP TABLE X")]
    [InlineData("DBCC CHECKDB")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=.;Trusted_Connection=yes;', 'SELECT 1')")]
    [InlineData("BULK INSERT ContractViewWithPaid FROM 'C:\\evil.csv'")]
    [InlineData("DECLARE @x INT")]
    public async Task SqlValidatorService_RedTeamPayloads_ThrowSecurityException(string maliciousSql)
    {
        var act = async () => await _validator.ValidateSqlAsync(maliciousSql, "tenant1");

        await act.Should().ThrowAsync<SecurityException>();
    }

    [Theory]
    [InlineData("EXEC xp_cmdshell 'whoami'")]
    [InlineData("EXEC sp_help")]
    [InlineData("SELECT * INTO Evil FROM ContractViewWithPaid")]
    [InlineData("WAITFOR DELAY '0:0:5'")]
    [InlineData("SELECT 1; DROP TABLE X")]
    [InlineData("DBCC CHECKDB")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=.;Trusted_Connection=yes;', 'SELECT 1')")]
    [InlineData("BULK INSERT ContractViewWithPaid FROM 'C:\\evil.csv'")]
    [InlineData("DECLARE @x INT")]
    public async Task SqlGuardPipeline_RedTeamPayloads_ThrowSecurityException(string maliciousSql)
    {
        var act = async () => await _pipeline.GuardAndHardenAsync(maliciousSql, "tenant1", companyId: 1);

        await act.Should().ThrowAsync<SecurityException>();
    }

    [Theory]
    [InlineData("SELECT c.ContractID, a.CustomerAccountName FROM ContractViewWithPaid c INNER JOIN CustomerAccountView a ON c.CustomerAccountID = a.CustomerAccountID WHERE c.ComID = @CurrentCompanyId")]
    [InlineData("SELECT TOP 50 c.ContractID, c.TotalOrder FROM ContractViewWithPaid c WHERE c.ComID = @CurrentCompanyId ORDER BY c.TotalOrder DESC")]
    [InlineData("SELECT c.ContractID, c.TotalOrder FROM ContractViewWithPaid c WHERE c.ComID = @CurrentCompanyId ORDER BY c.ContractID OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY")]
    [InlineData("SELECT c.CustomerAccountID, SUM(c.TotalOrder) AS TotalSum FROM ContractViewWithPaid c WHERE c.ComID = @CurrentCompanyId GROUP BY c.CustomerAccountID HAVING SUM(c.TotalOrder) > 100")]
    public async Task ValidReadOnlySelectQueries_PassSecurityVisitorValidatorAndPipeline(string validSql)
    {
        // 1. SecurityVisitor direct
        using var reader = new StringReader(validSql);
        var fragment = _parser.Parse(reader, out var errors);
        errors.Should().BeEmpty();
        var visitorAct = () => SecurityVisitor.ValidateReadOnlyScript(fragment);
        visitorAct.Should().NotThrow();

        // 2. SqlValidatorService
        var validatorAct = async () => await _validator.ValidateSqlAsync(validSql, "tenant1");
        await validatorAct.Should().NotThrowAsync();

        // 3. SqlGuardPipeline
        var result = await _pipeline.GuardAndHardenAsync(validSql, "tenant1", companyId: 42);
        result.RewrittenSql.Should().NotBeNullOrWhiteSpace();
        result.Parameters.Should().ContainKey("@CurrentCompanyId").WhoseValue.Should().Be(42);
    }
}

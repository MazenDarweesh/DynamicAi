namespace DynamicAI.Tests.Fixtures;

/// <summary>
/// Sample SQL queries for testing
/// Organized by category for easy reference
/// </summary>
public static class TestSqlQueries
{
    #region Valid SELECT Queries

    public static readonly string SimpleSelect = "SELECT * FROM Users WHERE Id = 1";

    public static readonly string SelectWithJoin = @"
        SELECT u.Name, o.Total 
        FROM Users u 
        INNER JOIN Orders o ON u.Id = o.UserId 
        WHERE o.Total > 100";

    public static readonly string SelectWithSubquery = @"
        SELECT * FROM Users 
        WHERE Id IN (SELECT UserId FROM Orders WHERE Total > 1000)";

    public static readonly string SelectWithAggregation = @"
        SELECT COUNT(*), SUM(Total), AVG(Total)
        FROM Orders 
        GROUP BY UserId 
        HAVING SUM(Total) > 5000";

    public static readonly string SelectWithCTE = @"
        WITH OrderTotals AS (
            SELECT UserId, SUM(Total) as TotalAmount
            FROM Orders
            GROUP BY UserId
        )
        SELECT u.Name, ot.TotalAmount
        FROM Users u
        JOIN OrderTotals ot ON u.Id = ot.UserId";

    public static readonly string ERPStyleQuery = @"
        SELECT TOP 100
            c.ContractID AS [رقم العقد],
            c.ContractDate AS [تاريخ العقد],
            c.Total AS [الإجمالي],
            g.GovName AS [المنطقة]
        FROM ContractViewWithPaid c
        LEFT JOIN GovView g ON c.Gov = g.GOVID
        WHERE c.ComID = @CurrentCompanyId
        ORDER BY c.ContractID DESC";

    #endregion

    #region Dangerous Queries (Should be blocked)

    public static readonly string DeleteStatement = "DELETE FROM Users WHERE Id = 5";

    public static readonly string UpdateStatement = "UPDATE Users SET Name = 'Hacked' WHERE 1=1";

    public static readonly string InsertStatement = "INSERT INTO Users (Name) VALUES ('Hacker')";

    public static readonly string DropTable = "DROP TABLE Users";

    public static readonly string TruncateTable = "TRUNCATE TABLE Users";

    public static readonly string AlterTable = "ALTER TABLE Users ADD HackerColumn VARCHAR(100)";

    public static readonly string CreateTable = "CREATE TABLE Hackers (Id INT)";

    public static readonly string GrantStatement = "GRANT SELECT ON Users TO PublicUser";

    public static readonly string ExecStatement = "EXEC sp_executesql N'DELETE FROM Users'";

    #endregion

    #region Edge Cases

    public static readonly string EmptyQuery = "";

    public static readonly string WhitespaceOnly = "   \n\t   ";

    public static readonly string InvalidSyntax = "SELEC * FORM Users";

    public static readonly string SqlInjectionAttempt = "SELECT * FROM Users WHERE Name = 'admin'; DROP TABLE Users; --'";

    #endregion
}

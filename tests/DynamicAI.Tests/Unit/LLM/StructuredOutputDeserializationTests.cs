using System.Text.Json;
using DynamicAi.Application.Models;
using FluentAssertions;

namespace DynamicAI.Tests.Unit.LLM;

/// <summary>
/// Unit tests verifying UnifiedLlmResponse JSON deserialization across intents,
/// clarification fields, schema linking, and casing variations.
/// </summary>
public class StructuredOutputDeserializationTests
{
    private static readonly JsonSerializerOptions CaseInsensitiveOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Deserialize_SqlDataRetrievalPayload_ShouldPopulateSqlIntentAndSchemaLinking()
    {
        const string json = """
            {
              "intent": "DataRetrieval",
              "schema_linking": [
                {
                  "user_term": "العميل لولو",
                  "mapped_to_column": "TM.CustomerAccountName",
                  "ar_label": "العميل",
                  "reason": "Direct customer name lookup"
                }
              ],
              "content": {
                "sql": "SELECT TOP 50 TM.ContractID, TM.TotalOrder FROM ContractViewWithPaid TM WHERE TM.ComID = @CurrentCompanyId",
                "message": "",
                "explanation": "استرجاع فواتير العميل لولو"
              },
              "confidence": 0.94,
              "needs_clarification": false,
              "clarification_question": null
            }
            """;

        var result = JsonSerializer.Deserialize<UnifiedLlmResponse>(json, CaseInsensitiveOptions);

        result.Should().NotBeNull();
        result!.Intent.Should().Be("DataRetrieval");
        result.IsSqlIntent.Should().BeTrue();
        result.IsChatIntent.Should().BeFalse();
        result.IsPredictionIntent.Should().BeFalse();
        result.NeedsClarification.Should().BeFalse();
        result.ClarificationQuestion.Should().BeNull();
        result.Confidence.Should().BeApproximately(0.94, 0.0001);
        result.Content.Sql.Should().Contain("ContractViewWithPaid");
        result.Content.Message.Should().BeEmpty();
        result.Content.Explanation.Should().Be("استرجاع فواتير العميل لولو");
        result.SchemaLinking.Should().ContainSingle();
        result.SchemaLinking![0].UserTerm.Should().Be("العميل لولو");
        result.SchemaLinking[0].MappedToColumn.Should().Be("TM.CustomerAccountName");
        result.SchemaLinking[0].ArLabel.Should().Be("العميل");
        result.SchemaLinking[0].Reason.Should().Be("Direct customer name lookup");
    }

    [Fact]
    public void Deserialize_ChatIntentPayload_ShouldSetIsChatIntentAndPopulateMessage()
    {
        const string json = """
            {
              "intent": "Chat",
              "schema_linking": [],
              "content": {
                "sql": "",
                "message": "مرحباً! يمكنني مساعدتك في تحليل فواتير المبيعات.",
                "explanation": "Conversational greeting"
              },
              "confidence": 0.99,
              "needs_clarification": false,
              "clarification_question": null
            }
            """;

        var result = JsonSerializer.Deserialize<UnifiedLlmResponse>(json, CaseInsensitiveOptions);

        result.Should().NotBeNull();
        result!.IsChatIntent.Should().BeTrue();
        result.IsSqlIntent.Should().BeFalse();
        result.Content.Sql.Should().BeEmpty();
        result.Content.Message.Should().Contain("فواتير المبيعات");
        result.SchemaLinking.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_ClarificationPayload_ShouldSetNeedsClarificationAndQuestion()
    {
        const string json = """
            {
              "intent": "DataRetrieval",
              "schema_linking": [],
              "content": {
                "sql": "",
                "message": "",
                "explanation": "Missing period filter"
              },
              "confidence": 0.42,
              "needs_clarification": true,
              "clarification_question": "هل تقصد فواتير الشهر الحالي أم العام الحالي؟"
            }
            """;

        var result = JsonSerializer.Deserialize<UnifiedLlmResponse>(json, CaseInsensitiveOptions);

        result.Should().NotBeNull();
        result!.NeedsClarification.Should().BeTrue();
        result.ClarificationQuestion.Should().Be("هل تقصد فواتير الشهر الحالي أم العام الحالي؟");
        result.Confidence.Should().BeLessThan(0.7);
        result.Content.Sql.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_PascalCasePayloadWithCaseInsensitiveOptions_ShouldDeserializeAllFields()
    {
        const string pascalJson = """
            {
              "Intent": "Analysis",
              "Schema_Linking": [
                {
                  "User_Term": "total sales",
                  "Mapped_To_Column": "TM.TotalOrder",
                  "Ar_Label": "قيمة الفاتورة",
                  "Reason": "Aggregating sales value"
                }
              ],
              "Content": {
                "Sql": "SELECT SUM(TM.TotalOrder) AS TotalSales FROM ContractViewWithPaid TM",
                "Message": "",
                "Explanation": "Sum of contract totals"
              },
              "Confidence": 0.91,
              "Needs_Clarification": false,
              "Clarification_Question": null
            }
            """;

        var result = JsonSerializer.Deserialize<UnifiedLlmResponse>(pascalJson, CaseInsensitiveOptions);

        result.Should().NotBeNull();
        result!.Intent.Should().Be("Analysis");
        result.IsSqlIntent.Should().BeTrue();
        result.Content.Sql.Should().Contain("SUM(TM.TotalOrder)");
        result.Content.Explanation.Should().Be("Sum of contract totals");
        result.Confidence.Should().BeApproximately(0.91, 0.0001);
        result.SchemaLinking.Should().ContainSingle();
        result.SchemaLinking![0].UserTerm.Should().Be("total sales");
        result.SchemaLinking[0].MappedToColumn.Should().Be("TM.TotalOrder");
    }

    [Theory]
    [InlineData("DataRetrieval", true, false, false, false)]
    [InlineData("Analysis", true, false, false, false)]
    [InlineData("PredictionWithDataRetrieval", true, false, false, true)]
    [InlineData("Chat", false, true, false, false)]
    [InlineData("Prediction", false, false, true, false)]
    [InlineData("Clarification", false, false, false, false)]
    public void IntentHelperProperties_ShouldClassifyIntentsAccurately(
        string intent,
        bool isSql,
        bool isChat,
        bool isPrediction,
        bool isPredictionWithData)
    {
        var response = new UnifiedLlmResponse { Intent = intent };

        response.IsSqlIntent.Should().Be(isSql);
        response.IsChatIntent.Should().Be(isChat);
        response.IsPredictionIntent.Should().Be(isPrediction);
        response.IsPredictionWithDataRetrievalIntent.Should().Be(isPredictionWithData);
    }
}

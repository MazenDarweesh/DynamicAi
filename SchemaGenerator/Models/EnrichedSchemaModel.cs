
namespace SchemaGenerator.Models
{
    // File: DynamicAi.Infrastructure/SchemaMapping/JsonSchemaModels.cs
    using System.Text.Json.Serialization;

    public class JsonSchemaRoot
    {
        [JsonPropertyName("schema")]
        public JsonSchema Schema { get; set; }
    }

    public class JsonSchema
    {
        [JsonPropertyName("entities")]
        public Dictionary<string, JsonEntity> Entities { get; set; }
    }

    public class JsonEntity
    {
        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("columns")]
        public Dictionary<string, JsonColumn> Columns { get; set; }
    }

    public class JsonColumn
    {
        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("synonyms")]
        public JsonSynonyms Synonyms { get; set; }
    }

    public class JsonSynonyms
    {
        [JsonPropertyName("en")]
        public List<string> En { get; set; }
        [JsonPropertyName("ar_fusha")]
        public List<string> ArFusha { get; set; }
        [JsonPropertyName("ar_eg")]
        public List<string> ArEg { get; set; }
        [JsonPropertyName("ar_sa")]
        public List<string> ArSa { get; set; }
    }
}

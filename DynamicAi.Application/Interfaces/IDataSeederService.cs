using System.Threading.Tasks;

namespace DynamicAi.Application.Interfaces
{
    public interface IDataSeederService
    {
        /// <summary>
        /// Generates synthetic training data using Gemini and seeds it into Qdrant.
        /// </summary>
        /// <param name="topic">The topic to generate data for (e.g., "Sales", "Inventory").</param>
        /// <param name="count">Number of examples to generate.</param>
        /// <returns>A summary of the operation.</returns>
        Task<string> SeedSyntheticDataAsync(string topic, int count = 50, string? tenantId = null, int? companyId = null);
    }
}

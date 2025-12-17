using System.Threading.Tasks;
using DynamicAi.Application.Models;

namespace DynamicAi.Application.Interfaces
{
    /// <summary>
    /// Interface for storing and retrieving user session context.
    /// Implementation can be In-Memory, Redis, or DB.
    /// </summary>
    public interface ISessionStore
    {
        Task<SessionContext?> GetSessionAsync(string sessionId);
        Task UpdateSessionAsync(string sessionId, string question, string sql, int? pageId = null);
    }
}

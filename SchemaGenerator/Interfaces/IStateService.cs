public interface IStateService
{
    Task<List<ProcessStateEntry>> LoadStateAsync();
    Task SaveStateAsync(List<ProcessStateEntry> currentState);
    Task UpdateEntryAsync(string viewName, Action<ProcessStateEntry> updateAction);
}
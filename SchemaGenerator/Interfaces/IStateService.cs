public interface IStateService
{
    Task<List<ProcessStateEntry>> LoadStateAsync(string stateFilePath);
    Task SaveStateAsync(string stateFilePath, List<ProcessStateEntry> currentState);
    Task UpdateEntryAsync(string stateFilePath, string viewName, Action<ProcessStateEntry> updateAction);
}
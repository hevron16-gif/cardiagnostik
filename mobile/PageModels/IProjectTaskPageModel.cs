using CarDiagnosticApp.Models;
using CommunityToolkit.Mvvm.Input;

namespace CarDiagnosticApp.PageModels
{
    public interface IProjectTaskPageModel
    {
        IAsyncRelayCommand<ProjectTask> NavigateToTaskCommand { get; }
        bool IsBusy { get; }
    }
}
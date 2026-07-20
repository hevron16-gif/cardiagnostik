using System;

namespace CarDiagnosticApp.Pages;

public partial class StubPage : ContentPage
{
    public StubPage()
    {
        InitializeComponent();
    }

    public StubPage(string title) : this()
    {
        LabelTitle.Text = title;
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        try
        {
            var nav = Navigation ?? Shell.Current?.Navigation;
            if (nav != null && nav.ModalStack.Count > 0)
            {
                await nav.PopModalAsync();
                return;
            }
            if (nav != null && nav.NavigationStack.Count > 1)
            {
                await nav.PopAsync();
                return;
            }
            if (Shell.Current != null)
                await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StubPage] back: {ex.Message}");
            try { await DisplayAlert("Назад", ex.Message, "OK"); } catch { }
        }
    }
}

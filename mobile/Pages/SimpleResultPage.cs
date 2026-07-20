namespace CarDiagnosticApp.Pages;

/// <summary>
/// Лёгкий экран результата диагностики (без сложного XAML/стилей/MaterialIcons).
/// Нужен для стабильности на Android.
/// </summary>
public class SimpleResultPage : ContentPage
{
    public SimpleResultPage(string diagnosisText, string errorCode, string brand, string model)
    {
        Title = "Диагностика AI";
        BackgroundColor = Color.FromArgb("#121212");

        var code = string.IsNullOrWhiteSpace(errorCode) ? "—" : errorCode.Trim();
        var car = $"{brand} {model}".Trim();
        if (string.IsNullOrWhiteSpace(car)) car = "Автомобиль";

        var text = string.IsNullOrWhiteSpace(diagnosisText)
            ? "Нет текста диагностики."
            : diagnosisText;

        var body = new VerticalStackLayout
        {
            Padding = new Thickness(16),
            Spacing = 14,
            Children =
            {
                new Label
                {
                    Text = code,
                    FontSize = 26,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb("#4D94FF"),
                },
                new Label
                {
                    Text = car,
                    FontSize = 15,
                    TextColor = Color.FromArgb("#BDBDBD"),
                },
                new BoxView
                {
                    HeightRequest = 1,
                    Color = Color.FromArgb("#333333"),
                    Margin = new Thickness(0, 4),
                },
                new Label
                {
                    Text = text,
                    FontSize = 15,
                    TextColor = Colors.White,
                },
            }
        };

        Content = new ScrollView { Content = body };
    }
}

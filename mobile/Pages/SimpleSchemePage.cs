namespace CarDiagnosticApp.Pages;

/// <summary>
/// Лёгкий экран location-схемы: только PNG из MauiAsset schemes/{code}.png.
/// Без DiagramDatabase / GraphicsView / сложного XAML.
/// </summary>
public class SimpleSchemePage : ContentPage
{
    private readonly string _code;
    private readonly Image _image;
    private readonly Label _status;

    public SimpleSchemePage(string errorCode, string brand, string model)
    {
        _code = (errorCode ?? "").Trim().ToUpperInvariant();
        Title = string.IsNullOrEmpty(_code) ? "Схема" : $"Схема {_code}";
        BackgroundColor = Color.FromArgb("#121212");

        var car = $"{brand} {model}".Trim();
        _status = new Label
        {
            Text = "Загрузка схемы…",
            FontSize = 13,
            TextColor = Color.FromArgb("#9E9E9E"),
            Margin = new Thickness(12, 0, 12, 8),
        };

        _image = new Image
        {
            Aspect = Aspect.AspectFit,
            BackgroundColor = Colors.White,
            MinimumHeightRequest = 280,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Start,
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new(GridLength.Auto),
                new(GridLength.Auto),
                new(GridLength.Star),
            },
            Children =
            {
                new Label
                {
                    Text = string.IsNullOrEmpty(car) ? _code : $"{_code} · {car}",
                    FontSize = 16,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Colors.White,
                    Margin = new Thickness(12, 12, 12, 4),
                }.WithRow(0),
                _status.WithRow(1),
                new ScrollView
                {
                    Content = new Border
                    {
                        Stroke = Color.FromArgb("#C62828"),
                        StrokeThickness = 2,
                        BackgroundColor = Colors.White,
                        Padding = 6,
                        Margin = new Thickness(8),
                        Content = _image,
                    }
                }.WithRow(2),
            }
        };

        Appearing += OnFirstAppear;
    }

    private bool _loaded;

    private async void OnFirstAppear(object? sender, EventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await LoadImageAsync();
    }

    private async Task LoadImageAsync()
    {
        if (string.IsNullOrEmpty(_code))
        {
            _status.Text = "Код ошибки не задан.";
            return;
        }

        try
        {
            byte[]? bytes = null;
            foreach (var name in new[] { $"schemes/{_code}.png", $"schemes/{_code}_location.png", $"{_code}.png" })
            {
                try
                {
                    await using var s = await FileSystem.OpenAppPackageFileAsync(name);
                    using var ms = new MemoryStream();
                    await s.CopyToAsync(ms);
                    if (ms.Length > 500)
                    {
                        bytes = ms.ToArray();
                        break;
                    }
                }
                catch { /* try next */ }
            }

            // Fallback: файл рядом с exe (Windows publish)
            if (bytes == null || bytes.Length < 500)
            {
                foreach (var dir in new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "Data", "schemes"),
                    Path.Combine(AppContext.BaseDirectory, "schemes"),
                })
                {
                    foreach (var n in new[] { $"{_code}.png", $"{_code}_location.png" })
                    {
                        var p = Path.Combine(dir, n);
                        if (!File.Exists(p)) continue;
                        bytes = await File.ReadAllBytesAsync(p);
                        if (bytes.Length > 500) break;
                    }
                    if (bytes is { Length: > 500 }) break;
                }
            }

            if (bytes == null || bytes.Length < 500)
            {
                _status.Text = $"PNG для {_code} не найден в библиотеке.";
                _image.BackgroundColor = Color.FromArgb("#263238");
                return;
            }

            var copy = bytes;
            _image.Source = null;
            _image.Source = ImageSource.FromStream(() => new MemoryStream(copy));
            _image.HeightRequest = 420;
            _image.MinimumHeightRequest = 300;
            _status.Text = $"LOCATION · {_code}.png · {bytes.Length / 1024} КБ";
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка загрузки: " + ex.Message;
        }
    }
}

internal static class ViewRowExt
{
    public static T WithRow<T>(this T view, int row) where T : BindableObject
    {
        Grid.SetRow(view, row);
        return view;
    }
}

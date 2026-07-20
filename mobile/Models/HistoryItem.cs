using Newtonsoft.Json;

namespace CarDiagnosticApp.Models
{
    /// <summary>
    /// Модель для десериализации ответа сервера (GET /history)
    /// и UI-привязок в HistoryPage.
    /// Статус теперь хранится в локальной SQLite (HistoryRecord),
    /// но для совместимости с XAML-привязками оставлен здесь.
    /// </summary>
    public class HistoryItem
    {
        // ----- Статусы -----
        public const string StatusUnsolved = "Не решено";
        public const string StatusInProgress = "В процессе";
        public const string StatusSolved = "Решено";

        private static readonly string[] StatusCycle = { StatusUnsolved, StatusInProgress, StatusSolved };

        // ----- Поля с сервера -----
        public string? error_code { get; set; }
        public string? car_brand { get; set; }
        public string? car_model { get; set; }
        public string? snippet { get; set; }
        public string? timestamp { get; set; }

        // ----- Статус (заполняется из БД) -----
        private string _status = StatusUnsolved;

        [JsonIgnore]
        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        /// <summary>ID записи в локальной SQLite (для обновления статуса).</summary>
        [JsonIgnore]
        public int DbId { get; set; }

        // ----- Вычисляемые свойства для UI -----

        [JsonIgnore]
        public Color StatusColor => Status switch
        {
            StatusSolved => Color.FromArgb("#4CAF50"),
            StatusInProgress => Color.FromArgb("#FFC107"),
            _ => Color.FromArgb("#9E9E9E")
        };

        [JsonIgnore]
        public Color StatusBackgroundColor => Status switch
        {
            StatusSolved => Color.FromArgb("#1B4CAF50"),
            StatusInProgress => Color.FromArgb("#1BFFC107"),
            _ => Color.FromArgb("#1B9E9E9E")
        };

        [JsonIgnore]
        public string DisplayDate
        {
            get
            {
                if (string.IsNullOrWhiteSpace(timestamp)) return "";
                if (DateTime.TryParse(timestamp, out var dt))
                    return dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
                return timestamp;
            }
        }

        [JsonIgnore]
        public string CarInfo =>
            $"{car_brand ?? ""} {car_model ?? ""}".Trim();

        /// <summary>
        /// Переключает статус по кругу.
        /// </summary>
        public void CycleStatus()
        {
            var idx = Array.IndexOf(StatusCycle, Status);
            Status = StatusCycle[(idx + 1) % StatusCycle.Length];
        }

        // ----- INotifyPropertyChanged (минимальный) -----
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }
}

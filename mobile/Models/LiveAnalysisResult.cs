using Newtonsoft.Json;

namespace CarDiagnosticApp.Models
{
    /// <summary>
    /// Один PID для отправки на AI-анализ.
    /// </summary>
    public class LivePidItem
    {
        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("value")]
        public double Value { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; } = "";

        [JsonProperty("min_val")]
        public double MinVal { get; set; }

        [JsonProperty("max_val")]
        public double MaxVal { get; set; }

        [JsonProperty("severity")]
        public int Severity { get; set; }  // 0=норма, 1=внимание, 2=опасно
    }

    /// <summary>
    /// Запрос на AI-анализ живых данных.
    /// </summary>
    public class LiveAnalyzeRequest
    {
        [JsonProperty("car_brand")]
        public string CarBrand { get; set; } = "";

        [JsonProperty("car_model")]
        public string CarModel { get; set; } = "";

        [JsonProperty("pids")]
        public List<LivePidItem> Pids { get; set; } = new();
    }

    /// <summary>
    /// Результат AI-анализа живых данных.
    /// </summary>
    public class LiveAnalysisResult
    {
        [JsonProperty("analysis")]
        public string Analysis { get; set; } = "";

        [JsonProperty("car")]
        public string Car { get; set; } = "";

        [JsonProperty("pid_count")]
        public int PidCount { get; set; }

        [JsonProperty("danger_count")]
        public int DangerCount { get; set; }

        [JsonProperty("warning_count")]
        public int WarningCount { get; set; }
    }
}

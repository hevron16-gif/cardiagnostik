namespace CarDiagnosticApp.Models
{
    public class DiagnosisResult
    {
        public string? diagnosis { get; set; }
        public string? error_code { get; set; }
        public string? car { get; set; }
        public string? source { get; set; }
        public bool has_clarifying_questions { get; set; }
    }
}

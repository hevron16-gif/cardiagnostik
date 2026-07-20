namespace CarDiagnosticApp.Models
{
    public class DiagnosisRequest
    {
        public string? error_code { get; set; }
        public string? car_brand { get; set; }
        public string? car_model { get; set; }
        public string? analytics_context { get; set; }
        public string? follow_up_context { get; set; }
    }
}
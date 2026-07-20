namespace CarDiagnosticApp.Models
{
    public class CarBrand
    {
        public string brand { get; set; } = string.Empty;
        public List<string> models { get; set; } = new();
    }
}

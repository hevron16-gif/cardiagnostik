using System.Collections.Generic;

namespace CarDiagnosticApp.Services
{
    public class TestModeService
    {
        private bool _isTestMode = false;
        private Dictionary<string, string> _testResponses;

        public TestModeService()
        {
            _testResponses = new Dictionary<string, string>
            {
                { "03", "43 01 34 02 00 00 00 00 00" },
                { "010C", "41 0C 1A 3E" },
                { "010D", "41 0D 4A" },
                { "0105", "41 05 5A" },
                { "010B", "41 0B 4B" },
            };
        }

        public void EnableTestMode() => _isTestMode = true;
        public void DisableTestMode() => _isTestMode = false;
        public bool IsTestMode() => _isTestMode;

        public string SendCommand(string command) =>
            _testResponses.ContainsKey(command) ? _testResponses[command] : "NO DATA";
    }
}

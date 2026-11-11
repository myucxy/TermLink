// Shared session presentation does not depend on Windows IPC or its SID contracts.
namespace TermLink.Agent
{
    internal sealed class DesktopAiSessionSummary
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Agent { get; set; }
        public string Directory { get; set; }
        public string State { get; set; }
    }
}

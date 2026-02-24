using Nexa.Adapter.Models;

namespace Nexa.Adapter.Services
{
    public interface IConfidenceScoreEngine
    {
        public double Calculate(AlertInvestigationContext input, EvidenceResult evidence);
    }
    public class ConfidenceScoreEngine: IConfidenceScoreEngine
    {
        private static readonly Random _random = new();

        public double Calculate(AlertInvestigationContext input, EvidenceResult evidence)
        {
            // Random score between 0.0 and 1.0
            return Math.Round(_random.NextDouble(), 2);
        }
    }

}

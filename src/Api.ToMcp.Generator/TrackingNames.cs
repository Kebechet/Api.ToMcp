namespace Api.ToMcp.Generator
{
    /// <summary>
    /// The names of the generator's pipeline steps. A driver created with <c>trackIncrementalGeneratorSteps</c> reports
    /// under them why each step ran, or that it reused its output from the run before.
    /// </summary>
    internal static class TrackingNames
    {
        public const string Config = "Config";
        public const string Controllers = "Controllers";
    }
}

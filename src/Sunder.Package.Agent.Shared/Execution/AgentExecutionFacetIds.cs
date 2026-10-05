namespace Sunder.Package.Agent.Protocol;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public static class AgentExecutionFacetIds
{
    public const string ProcessExecution = "process-execution";
    public const string ResourceResolution = "resource-resolution";
    public const string ResourceAuthority = "resource-authority";
    public const string ExecutionScope = "execution-scope";
    public const string PathMapping = "path-mapping";
    public const string PathEnvironment = "path-environment";
    public const string RangedFileRead = "ranged-file-read";
    public const string StructuredFileSearch = "structured-file-search";
    public const string ScopedInstructionDiscovery = "scoped-instruction-discovery";
}

namespace DataGateOpenVpnManager.Tests.Services;

/// <summary>
/// OpenVpnProcessService uses process-wide static gate/operation state (one daemon per container).
/// Serialize these tests so snapshots do not race across cases.
/// </summary>
[CollectionDefinition(nameof(OpenVpnProcessServiceTests), DisableParallelization = true)]
public sealed class OpenVpnProcessServiceTestsCollection;

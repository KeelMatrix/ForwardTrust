using System.Net;
using KeelMatrix.ForwardTrust;

namespace KeelMatrix.ForwardTrust.Tests;

public sealed class PublicSurfaceTests
{
    [Fact]
    public void ApprovedPublicConceptsExistWithoutFrameworkTestHostTypes()
    {
        var assembly = typeof(ForwardTrustVerifier).Assembly;
        var publicTypes = assembly.GetExportedTypes()
            .Where(type => type.Namespace == "KeelMatrix.ForwardTrust")
            .Select(type => type.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.DoesNotContain(publicTypes, name => name.Contains("TestHost", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nameof(ForwardTrustScenario), publicTypes);
        Assert.Contains(nameof(ForwardTrustControl), publicTypes);
        Assert.Contains(nameof(ForwardedIdentity), publicTypes);
        Assert.Contains(nameof(ForwardTrustVerifier), publicTypes);
        Assert.Contains(nameof(ForwardTrustResult), publicTypes);
        Assert.Contains(nameof(ForwardTrustFailure), publicTypes);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Microsoft.AspNetCore.TestHost");
    }
}

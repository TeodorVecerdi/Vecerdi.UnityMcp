using UnityMcp;
using Xunit;

namespace UnityMcp.Tests;

public sealed class PortResolverTests {
    private static EditorInstance Editor(int port) => new() { Port = port, ProjectName = $"Project{port}" };

    [Fact]
    public void Resolve_ExplicitPort_UsesItRegardlessOfDefaultOrDiscovery() {
        var result = PortResolver.Resolve(explicitPort: 9101, defaultPort: 9200, [Editor(9300), Editor(9400)]);

        Assert.True(result.IsResolved);
        Assert.Equal(9101, result.Port);
    }

    [Fact]
    public void Resolve_ExplicitPort_BeatsDefault() {
        var result = PortResolver.Resolve(explicitPort: 9101, defaultPort: 9200, [Editor(9101), Editor(9200)]);

        Assert.Equal(9101, result.Port);
    }

    [Fact]
    public void Resolve_NoExplicit_UsesDefault() {
        var result = PortResolver.Resolve(explicitPort: null, defaultPort: 9200, [Editor(9200), Editor(9300)]);

        Assert.True(result.IsResolved);
        Assert.Equal(9200, result.Port);
    }

    [Fact]
    public void Resolve_NoExplicitNoDefault_SingleEditor_AutoResolves() {
        var result = PortResolver.Resolve(explicitPort: null, defaultPort: null, [Editor(9100)]);

        Assert.True(result.IsResolved);
        Assert.Equal(9100, result.Port);
    }

    [Fact]
    public void Resolve_NoExplicitNoDefault_NoEditors_Fails() {
        var result = PortResolver.Resolve(explicitPort: null, defaultPort: null, []);

        Assert.False(result.IsResolved);
        Assert.Null(result.Port);
        Assert.Contains("No Unity Editor", result.Error);
    }

    [Fact]
    public void Resolve_NoExplicitNoDefault_MultipleEditors_FailsWithGuidance() {
        var result = PortResolver.Resolve(explicitPort: null, defaultPort: null, [Editor(9100), Editor(9200)]);

        Assert.False(result.IsResolved);
        Assert.Contains("Multiple Unity Editors found (2)", result.Error);
        Assert.Contains("port", result.Error);
        Assert.Contains("select_editor", result.Error);
    }

    [Fact]
    public void Resolve_DefaultTakesPrecedence_OverMultipleDiscovered() {
        var result = PortResolver.Resolve(explicitPort: null, defaultPort: 9300, [Editor(9100), Editor(9200), Editor(9300)]);

        Assert.Equal(9300, result.Port);
    }
}

using UnityMcp;
using Xunit;

namespace UnityMcp.Tests;

public sealed class CompilerDiagnosticTests {
    [Fact]
    public void TryParse_LocatedError_ExtractsAllFields() {
        var ok = CompilerDiagnostic.TryParse(
            "Assets/Scripts/Foo.cs(12,20): error CS0103: The name 'x' does not exist in the current context",
            out var diagnostic);

        Assert.True(ok);
        Assert.Equal("Assets/Scripts/Foo.cs", diagnostic.File);
        Assert.Equal(12, diagnostic.Line);
        Assert.Equal(20, diagnostic.Column);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal("CS0103", diagnostic.Code);
        Assert.Equal("The name 'x' does not exist in the current context", diagnostic.Message);
        Assert.True(diagnostic.HasLocation);
    }

    [Fact]
    public void TryParse_WindowsBackslashPath_ParsesFile() {
        var ok = CompilerDiagnostic.TryParse(
            @"Assets\Scripts\Bar.cs(3,5): warning CS0168: variable declared but never used",
            out var diagnostic);

        Assert.True(ok);
        Assert.Equal(@"Assets\Scripts\Bar.cs", diagnostic.File);
        Assert.Equal("warning", diagnostic.Severity);
        Assert.Equal("CS0168", diagnostic.Code);
    }

    [Fact]
    public void TryParse_StripsTrailingAssemblyAnnotation() {
        var ok = CompilerDiagnostic.TryParse(
            @"Assets/Scripts/Foo.cs(1,1): error CS1002: ; expected [D:\proj\Assembly-CSharp.csproj]",
            out var diagnostic);

        Assert.True(ok);
        Assert.Equal("; expected", diagnostic.Message);
        Assert.Equal(@"D:\proj\Assembly-CSharp.csproj", diagnostic.Assembly);
    }

    [Fact]
    public void TryParse_BareError_WithoutLocation() {
        var ok = CompilerDiagnostic.TryParse(
            "error CS0006: Metadata file 'Foo.dll' could not be found",
            out var diagnostic);

        Assert.True(ok);
        Assert.False(diagnostic.HasLocation);
        Assert.Null(diagnostic.File);
        Assert.Equal("CS0006", diagnostic.Code);
        Assert.Equal("Metadata file 'Foo.dll' could not be found", diagnostic.Message);
    }

    [Fact]
    public void TryParse_MultilineMessage_UsesFirstLineButKeepsRaw() {
        var raw = "Assets/Scripts/Foo.cs(9,13): error CS1519: Invalid token\n  caret line\n  more context";
        var ok = CompilerDiagnostic.TryParse(raw, out var diagnostic);

        Assert.True(ok);
        Assert.Equal("Invalid token", diagnostic.Message);
        Assert.Equal(raw, diagnostic.Raw);
    }

    [Theory]
    [InlineData("Just a normal debug log line")]
    [InlineData("NullReferenceException: Object reference not set to an instance of an object")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_NonDiagnostic_ReturnsFalse(string? message) {
        Assert.False(CompilerDiagnostic.TryParse(message, out _));
    }

    [Fact]
    public void ToDisplayLine_RendersLocationSeverityCodeMessage() {
        CompilerDiagnostic.TryParse(
            "Assets/Scripts/Foo.cs(12,20): error CS0103: The name 'x' does not exist",
            out var diagnostic);

        Assert.Equal(
            "Assets/Scripts/Foo.cs(12,20): error CS0103: The name 'x' does not exist",
            diagnostic.ToDisplayLine());
    }

    [Fact]
    public void ToDisplayLine_BareDiagnostic_OmitsLocation() {
        CompilerDiagnostic.TryParse("error CS0006: missing metadata", out var diagnostic);
        Assert.Equal("error CS0006: missing metadata", diagnostic.ToDisplayLine());
    }
}

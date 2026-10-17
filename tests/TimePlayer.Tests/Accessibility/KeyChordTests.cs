using TimePlayer.Core.Accessibility;
using Xunit;

namespace TimePlayer.Tests.Accessibility;

public class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+F", KeyModifiers.Ctrl, "F")]
    [InlineData("ctrl+f", KeyModifiers.Ctrl, "F")]
    [InlineData("Control+Shift+P", KeyModifiers.Ctrl | KeyModifiers.Shift, "P")]
    [InlineData("Alt+Return", KeyModifiers.Alt, "Enter")]
    [InlineData("spacebar", KeyModifiers.None, "Space")]
    [InlineData("Ctrl+Alt+Shift+Win+K", KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Win, "K")]
    public void TryParse_ParsesValidChords(string input, KeyModifiers expectedModifiers, string expectedKey)
    {
        var success = KeyChord.TryParse(input, out var chord);

        Assert.True(success);
        Assert.NotNull(chord);
        Assert.Equal(expectedModifiers, chord.Modifiers);
        Assert.Equal(expectedKey, chord.Key);
    }

    [Fact]
    public void ToString_CanonicalizesModifierOrder()
    {
        // Out of standard order: Shift then Alt then Ctrl
        var success = KeyChord.TryParse("Shift+Alt+Ctrl+T", out var chord);

        Assert.True(success);
        // Canonical order must be Ctrl+Alt+Shift+T
        Assert.Equal("Ctrl+Alt+Shift+T", chord.ToString());
    }

    [Fact]
    public void ToAccessibleSpeech_ProvidesSpokenWords()
    {
        var chord = KeyChord.Parse("Ctrl+Shift+P");
        var speech = chord.ToAccessibleSpeech();

        Assert.Equal("Control plus Shift plus P", speech);
    }

    [Fact]
    public void Equals_IsCaseInsensitiveForKeys()
    {
        var chord1 = KeyChord.Parse("Ctrl+K");
        var chord2 = KeyChord.Parse("ctrl+k");

        Assert.Equal(chord1, chord2);
        Assert.True(chord1 == chord2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+A+B")]
    public void TryParse_FailsGracefullyForInvalidInputs(string? input)
    {
        var success = KeyChord.TryParse(input, out var chord);
        Assert.False(success);
        Assert.Null(chord);
    }
}

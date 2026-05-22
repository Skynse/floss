namespace Floss.App.Tests;

public class KeyBindingTests
{
    [Fact]
    public void Parse_HandlesFriendlyNamesAndModifiers()
    {
        var binding = AppKeyBinding.Parse("Ctrl+Shift+[");
        TestAssertions.Equal(Key.OemOpenBrackets, binding.Key);
        TestAssertions.Equal(KeyModifiers.Control | KeyModifiers.Shift, binding.Modifiers);
        TestAssertions.True(AppKeyBinding.Parse("Alt").IsModifierOnly);
        TestAssertions.True(AppKeyBinding.Parse("not-a-key").IsEmpty);
    }

    [Fact]
    public void ToStringAndDisplay_ReturnExpectedText()
    {
        TestAssertions.Equal("Ctrl+Del", new AppKeyBinding(Key.Delete, KeyModifiers.Control).ToString());
        TestAssertions.Equal("--", AppKeyBinding.Empty.Display());
    }

    [Fact]
    public void Matches_HandlesModifierOnlyBindings()
    {
        TestAssertions.True(new AppKeyBinding(Key.None, KeyModifiers.Alt).Matches(Key.A, KeyModifiers.Alt));
        TestAssertions.False(new AppKeyBinding(Key.A, KeyModifiers.Alt).Matches(Key.A, KeyModifiers.None));
        TestAssertions.False(AppKeyBinding.Empty.Matches(Key.A, KeyModifiers.None));
    }

    [Fact]
    public void ModifierHelpers_UpdateModifierFlags()
    {
        var modifiers = AppKeyBinding.ModifiersWithKeyDown(Key.LeftCtrl, KeyModifiers.Shift);
        TestAssertions.Equal(KeyModifiers.Control | KeyModifiers.Shift, modifiers);
        TestAssertions.Equal(KeyModifiers.Shift, AppKeyBinding.ModifiersAfterKeyUp(Key.RightCtrl, modifiers));
    }

    [Fact]
    public void JsonConverter_RoundTrips()
    {
        var json = JsonSerializer.Serialize(new AppKeyBinding(Key.OemComma, KeyModifiers.Control));
        TestAssertions.Equal("\"Ctrl\\u002B,\"", json);
        var binding = JsonSerializer.Deserialize<AppKeyBinding>(json);
        TestAssertions.Equal(Key.OemComma, binding!.Key);
        TestAssertions.Equal(KeyModifiers.Control, binding.Modifiers);
    }
}


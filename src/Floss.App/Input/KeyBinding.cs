using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;

namespace Floss.App.Input;

[JsonConverter(typeof(KeyBindingConverter))]
public sealed class KeyBinding
{
    public static readonly KeyBinding Empty = new(Key.None, KeyModifiers.None);

    public Key Key { get; }
    public KeyModifiers Modifiers { get; }
    public bool IsEmpty => Key == Key.None && Modifiers == KeyModifiers.None;
    public bool IsModifierOnly => Key == Key.None && Modifiers != KeyModifiers.None;

    public KeyBinding(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        Key = key;
        Modifiers = modifiers;
    }

    public bool Matches(Key key, KeyModifiers mods) =>
        !IsEmpty && Modifiers == mods && (IsModifierOnly || Key == key);

    public bool Matches(KeyEventArgs e) =>
        Matches(e.Key, e.KeyModifiers);

    public static KeyModifiers ModifiersWithKeyDown(Key key, KeyModifiers modifiers) =>
        key switch
        {
            Key.LeftCtrl or Key.RightCtrl => modifiers | KeyModifiers.Control,
            Key.LeftAlt or Key.RightAlt => modifiers | KeyModifiers.Alt,
            Key.LeftShift or Key.RightShift => modifiers | KeyModifiers.Shift,
            Key.LWin or Key.RWin => modifiers | KeyModifiers.Meta,
            _ => modifiers
        };

    public static KeyModifiers ModifiersAfterKeyUp(Key key, KeyModifiers modifiers) =>
        key switch
        {
            Key.LeftCtrl or Key.RightCtrl => modifiers & ~KeyModifiers.Control,
            Key.LeftAlt or Key.RightAlt => modifiers & ~KeyModifiers.Alt,
            Key.LeftShift or Key.RightShift => modifiers & ~KeyModifiers.Shift,
            Key.LWin or Key.RWin => modifiers & ~KeyModifiers.Meta,
            _ => modifiers
        };

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Key != Key.None) parts.Add(KeyToString(Key));
        return string.Join("+", parts);
    }

    public string Display() => IsEmpty ? "--" : ToString();

    public static KeyBinding Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Empty;

        var parts = s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mods = KeyModifiers.None;
        Key? key = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= KeyModifiers.Control; break;
                case "alt": mods |= KeyModifiers.Alt; break;
                case "shift": mods |= KeyModifiers.Shift; break;
                default:
                    key = StringToKey(part);
                    break;
            }
        }

        if (key.HasValue && key.Value != Key.None) return new KeyBinding(key.Value, mods);
        return mods != KeyModifiers.None ? new KeyBinding(Key.None, mods) : Empty;
    }

    // ── Key name tables ───────────────────────────────────────────────────────

    private static string KeyToString(Key key) =>
        FriendlyNames.TryGetValue(key, out var name) ? name : key.ToString();

    private static Key StringToKey(string name)
    {
        if (ReverseFriendlyNames.TryGetValue(name, out var k)) return k;
        return Enum.TryParse<Key>(name, ignoreCase: true, out var result) ? result : Key.None;
    }

    private static readonly Dictionary<Key, string> FriendlyNames = new()
    {
        [Key.OemOpenBrackets] = "[",
        [Key.OemCloseBrackets] = "]",
        [Key.OemPeriod] = ".",
        [Key.OemComma] = ",",
        [Key.OemMinus] = "-",
        [Key.OemPlus] = "=",
        [Key.OemSemicolon] = ";",
        [Key.OemQuotes] = "'",
        [Key.OemBackslash] = "\\",
        [Key.OemTilde] = "`",
        [Key.D0] = "0",
        [Key.D1] = "1",
        [Key.D2] = "2",
        [Key.D3] = "3",
        [Key.D4] = "4",
        [Key.D5] = "5",
        [Key.D6] = "6",
        [Key.D7] = "7",
        [Key.D8] = "8",
        [Key.D9] = "9",
        [Key.Add] = "NumPlus",
        [Key.Subtract] = "NumMinus",
        [Key.Multiply] = "NumMul",
        [Key.Divide] = "NumDiv",
        [Key.Delete] = "Del",
        [Key.Return] = "Enter",
        [Key.Prior] = "PageUp",
        [Key.Next] = "PageDown",
        [Key.Back] = "Backspace",
        [Key.Space] = "Space",
        [Key.Tab] = "Tab",
        [Key.Escape] = "Esc",
    };

    private static readonly Dictionary<string, Key> ReverseFriendlyNames;

    static KeyBinding()
    {
        ReverseFriendlyNames = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in FriendlyNames)
            ReverseFriendlyNames.TryAdd(v, k);
    }
}

public sealed class KeyBindingConverter : JsonConverter<KeyBinding>
{
    public override KeyBinding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => KeyBinding.Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, KeyBinding value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

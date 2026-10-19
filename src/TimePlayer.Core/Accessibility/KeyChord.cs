using System.Text;

namespace TimePlayer.Core.Accessibility;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,
    Win = 1 << 3
}

/// <summary>
/// Immutable representation of a keyboard shortcut / chord, inspired by the trypsynth key-chord architecture.
/// Canonicalizes modifier ordering (Ctrl+Alt+Shift+Win+Key), standardizes key names, and supports
/// screen reader friendly shortcut display and matching.
/// </summary>
public sealed record KeyChord : IEquatable<KeyChord>
{
    public KeyModifiers Modifiers { get; }
    public string Key { get; }

    public KeyChord(KeyModifiers modifiers, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key cannot be empty.", nameof(key));

        Modifiers = modifiers;
        Key = NormalizeKeyName(key.Trim());
    }

    public static KeyChord Parse(string text)
    {
        if (TryParse(text, out var chord))
            return chord;

        throw new FormatException($"Invalid key chord representation: '{text}'");
    }

    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var tokens = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        var modifiers = KeyModifiers.None;
        string? mainKey = null;

        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var lower = token.ToLowerInvariant();

            if (lower is "ctrl" or "control")
            {
                modifiers |= KeyModifiers.Ctrl;
            }
            else if (lower is "alt" or "menu")
            {
                modifiers |= KeyModifiers.Alt;
            }
            else if (lower is "shift")
            {
                modifiers |= KeyModifiers.Shift;
            }
            else if (lower is "win" or "windows" or "meta")
            {
                modifiers |= KeyModifiers.Win;
            }
            else
            {
                // Last token is expected to be the main key
                if (mainKey != null)
                {
                    // Multiple non-modifier keys in a single chord is invalid
                    return false;
                }
                mainKey = token;
            }
        }

        if (string.IsNullOrWhiteSpace(mainKey))
            return false;

        chord = new KeyChord(modifiers, mainKey);
        return true;
    }

    public static string NormalizeKeyName(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower switch
        {
            "return" => "Enter",
            "esc" or "escape" => "Esc",
            "spacebar" => "Space",
            "space" => "Space",
            "prior" => "PageUp",
            "next" => "PageDown",
            "back" or "backspace" => "Backspace",
            "delete" or "del" => "Del",
            "insert" or "ins" => "Ins",
            "left" or "arrowleft" => "Left",
            "right" or "arrowright" => "Right",
            "up" or "arrowup" => "Up",
            "down" or "arrowdown" => "Down",
            "home" => "Home",
            "end" => "End",
            "tab" => "Tab",
            _ => key.Length == 1 ? char.ToUpperInvariant(key[0]).ToString() : Capitalize(key)
        };
    }

    private static string Capitalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(KeyModifiers.Ctrl)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(KeyModifiers.Win)) sb.Append("Win+");
        sb.Append(Key);
        return sb.ToString();
    }

    public bool Equals(KeyChord? other)
    {
        if (other is null) return false;
        return Modifiers == other.Modifiers &&
               string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Modifiers, StringComparer.OrdinalIgnoreCase.GetHashCode(Key));
    }
}

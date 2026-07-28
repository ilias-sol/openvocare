using System.Text;

namespace OpenVocare.Services;

internal static class DeliveredTextSanitizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder? sanitized = null;
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (IsMalformedApostrophe(character)
                && IsWordCharacter(text, index - 1)
                && IsWordCharacter(text, index + 1))
            {
                sanitized ??= new StringBuilder(text.Length).Append(text, 0, index);
                sanitized.Append('\'');
                continue;
            }
            if (char.IsControl(character)
                && character is not '\r' and not '\n' and not '\t')
            {
                sanitized ??= new StringBuilder(text.Length).Append(text, 0, index);
                continue;
            }
            sanitized?.Append(character);
        }

        return (sanitized?.ToString() ?? text).Trim();
    }

    private static bool IsMalformedApostrophe(char character) =>
        character is '\u0018' or '\u0019' or '\u0091' or '\u0092';

    private static bool IsWordCharacter(string text, int index) =>
        index >= 0
        && index < text.Length
        && char.IsLetterOrDigit(text[index]);
}

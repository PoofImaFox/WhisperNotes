using System.Globalization;
using System.Text;

namespace WhisperNotes.Core.Notes;

/// <summary>
/// The YAML front matter both exporters write.
/// </summary>
/// <remarks>
/// Front matter is the one part of an export that a machine reads back, so a title containing
/// <c>:</c>, <c>#</c> or <c>---</c> restructuring the document is a real failure rather than a
/// cosmetic one. Shared rather than duplicated per exporter because the two had already drifted:
/// the same control character came out as <c>\xNN</c> in one and <c>\uNNNN</c> in the other.
/// </remarks>
internal static class Yaml
{
    /// <summary>Always emits a double-quoted scalar, so anything the value contains stays inert.</summary>
    internal static string Scalar(string? value)
    {
        var builder = new StringBuilder((value?.Length ?? 0) + 2);
        builder.Append('"');

        foreach (var character in value ?? string.Empty)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}

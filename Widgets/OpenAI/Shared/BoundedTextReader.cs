using System.Text;

namespace OpenAIUsage;

/// <summary>Frames newline JSON without allocating an unbounded line from a child process.</summary>
public sealed class BoundedTextReader(TextReader reader, int maximumCharacters)
{
    private readonly char[] buffer = new char[4096];
    private int position, available;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (position == available)
            {
                available = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                position = 0;
                if (available == 0) return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
            }
            int newline = Array.IndexOf(buffer, '\n', position, available - position);
            int count = (newline < 0 ? available : newline) - position;
            if (line.Length + count > maximumCharacters)
                throw new InvalidDataException("The JSON message exceeds the supported size.");
            line.Append(buffer, position, count);
            position += count;
            if (newline >= 0)
            {
                position++;
                return line.ToString().TrimEnd('\r');
            }
        }
    }

    public static async Task<string> ReadToEndAsync(TextReader reader, int maximumCharacters,
        CancellationToken cancellationToken = default)
    {
        var result = new StringBuilder();
        var chunk = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(chunk.AsMemory(), cancellationToken)) != 0)
        {
            if (result.Length + count > maximumCharacters)
                throw new InvalidDataException("The settings request exceeds the supported size.");
            result.Append(chunk, 0, count);
        }
        return result.ToString();
    }
}

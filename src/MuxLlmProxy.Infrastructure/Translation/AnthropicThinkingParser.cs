namespace MuxLlmProxy.Infrastructure.Translation;

internal enum ContentChunkType
{
    Text,
    Thinking
}

internal sealed record ContentChunk(ContentChunkType Type, string Content);

internal sealed class AnthropicThinkingParser
{
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";
    private string _buffer = string.Empty;
    private bool _inThinkTag;

    public IEnumerable<ContentChunk> Feed(string content)
    {
        _buffer += content;
        while (_buffer.Length > 0)
        {
            var previousLength = _buffer.Length;
            var chunk = _inThinkTag ? ParseInsideThink() : ParseOutsideThink();
            if (chunk is not null)
            {
                yield return chunk;
            }
            else if (_buffer.Length == previousLength)
            {
                yield break;
            }
        }
    }

    public ContentChunk? Flush()
    {
        if (_buffer.Length == 0)
        {
            return null;
        }

        var chunk = new ContentChunk(_inThinkTag ? ContentChunkType.Thinking : ContentChunkType.Text, _buffer);
        _buffer = string.Empty;
        return chunk;
    }

    private ContentChunk? ParseOutsideThink()
    {
        var thinkStart = _buffer.IndexOf(OpenTag, StringComparison.Ordinal);
        var orphanClose = _buffer.IndexOf(CloseTag, StringComparison.Ordinal);
        if (orphanClose >= 0 && (thinkStart < 0 || orphanClose < thinkStart))
        {
            var preOrphan = _buffer[..orphanClose];
            _buffer = _buffer[(orphanClose + CloseTag.Length)..];
            return preOrphan.Length == 0 ? null : new ContentChunk(ContentChunkType.Text, preOrphan);
        }

        if (thinkStart < 0)
        {
            var lastBracket = _buffer.LastIndexOf('<');
            if (lastBracket >= 0)
            {
                var potential = _buffer[lastBracket..];
                if ((potential.Length < OpenTag.Length && OpenTag.StartsWith(potential, StringComparison.Ordinal))
                    || (potential.Length < CloseTag.Length && CloseTag.StartsWith(potential, StringComparison.Ordinal)))
                {
                    var emit = _buffer[..lastBracket];
                    _buffer = _buffer[lastBracket..];
                    return emit.Length == 0 ? null : new ContentChunk(ContentChunkType.Text, emit);
                }
            }

            var output = _buffer;
            _buffer = string.Empty;
            return output.Length == 0 ? null : new ContentChunk(ContentChunkType.Text, output);
        }

        var preThink = _buffer[..thinkStart];
        _buffer = _buffer[(thinkStart + OpenTag.Length)..];
        _inThinkTag = true;
        return preThink.Length == 0 ? null : new ContentChunk(ContentChunkType.Text, preThink);
    }

    private ContentChunk? ParseInsideThink()
    {
        var thinkEnd = _buffer.IndexOf(CloseTag, StringComparison.Ordinal);
        if (thinkEnd < 0)
        {
            var lastBracket = _buffer.LastIndexOf('<');
            if (lastBracket >= 0 && _buffer.Length - lastBracket < CloseTag.Length)
            {
                var potential = _buffer[lastBracket..];
                if (CloseTag.StartsWith(potential, StringComparison.Ordinal))
                {
                    var emit = _buffer[..lastBracket];
                    _buffer = _buffer[lastBracket..];
                    return emit.Length == 0 ? null : new ContentChunk(ContentChunkType.Thinking, emit);
                }
            }

            var output = _buffer;
            _buffer = string.Empty;
            return output.Length == 0 ? null : new ContentChunk(ContentChunkType.Thinking, output);
        }

        var thinking = _buffer[..thinkEnd];
        _buffer = _buffer[(thinkEnd + CloseTag.Length)..];
        _inThinkTag = false;
        return thinking.Length == 0 ? null : new ContentChunk(ContentChunkType.Thinking, thinking);
    }
}

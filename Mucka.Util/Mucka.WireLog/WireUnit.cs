namespace Mucka.WireLog;

/// <summary>
/// The rx reads of one prompt-bounded stretch of server output, as <see cref="WireLogWriter"/> holds
/// them while they arrive and as <c>WireHeal</c> rebuilds them from a stored run: one rule, so a log
/// joined live and a log joined later agree on what a unit is.
///
/// <para>A unit is the rx rows since the last <see cref="Close"/> or <see cref="Abandon"/>. A unit that
/// passes <see cref="MaxBytes"/> or <see cref="MaxSpan"/> is lost: it is left as the reads it arrived
/// in, and holds nothing more until it ends.</para>
/// </summary>
public sealed class WireUnit
{
    /// <summary>The most bytes one unit is held to, so a stream that never closes one cannot grow
    /// without end.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The widest span of <c>seq</c> one unit may cover, which bounds how far back from the end
    /// of the table a merge looks for its rows.</summary>
    public const int MaxSpan = 1024;

    private readonly List<(long Seq, byte[] Data)> _rows = [];
    private int _bytes;
    private bool _lost;

    /// <summary>Adds the next rx read.</summary>
    public void Add(long seq, byte[] data)
    {
        if (_lost) return;
        if ((_rows.Count > 0 && seq - _rows[0].Seq >= MaxSpan) || _bytes + data.Length > MaxBytes)
        {
            _rows.Clear();
            _bytes = 0;
            _lost = true;
            return;
        }
        _rows.Add((seq, data));
        _bytes += data.Length;
    }

    /// <summary>Ends the unit. True when it held more than one read and was not lost: then
    /// <paramref name="seqs"/> are their <c>seq</c> in order and <paramref name="joined"/> their bytes
    /// end to end. Either way the next read starts a new unit.</summary>
    public bool Close(out long[] seqs, out byte[] joined)
    {
        var joins = !_lost && _rows.Count > 1;
        if (joins)
        {
            joined = new byte[_bytes];
            seqs = new long[_rows.Count];
            var at = 0;
            for (var i = 0; i < _rows.Count; i++)
            {
                seqs[i] = _rows[i].Seq;
                Buffer.BlockCopy(_rows[i].Data, 0, joined, at, _rows[i].Data.Length);
                at += _rows[i].Data.Length;
            }
        }
        else
        {
            seqs = [];
            joined = [];
        }
        Abandon();
        return joins;
    }

    /// <summary>Forgets the reads held and leaves them as they arrived.</summary>
    public void Abandon()
    {
        _rows.Clear();
        _bytes = 0;
        _lost = false;
    }
}

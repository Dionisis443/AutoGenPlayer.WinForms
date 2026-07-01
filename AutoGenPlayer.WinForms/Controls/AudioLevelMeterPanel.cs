using AutoGen_Player.Audio;
using AutoGen_Player.Core;

namespace AutoGen_Player.Controls;

internal sealed class AudioLevelMeterPanel : Control
{
    private readonly System.Windows.Forms.Timer _decayTimer = new();
    private IReadOnlyList<AudioMixSelection> _selections = [];
    private StereoLevel[] _streamLevels = [];
    private StereoLevel _mixedLevel;

    public AudioLevelMeterPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(18, 18, 18);
        ForeColor = Color.WhiteSmoke;

        _decayTimer.Interval = 40;
        _decayTimer.Tick += (_, _) => DecayLevels();
        _decayTimer.Start();
    }

    public void SetStreams(IReadOnlyList<AudioMixSelection> selections)
    {
        _selections = selections.ToArray();
        _streamLevels = new StereoLevel[_selections.Count];
        _mixedLevel = default;
        Invalidate();
    }

    public void UpdateLevels(AudioLevelSnapshotEventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => UpdateLevels(e)));
            return;
        }

        _selections = e.Selections.ToArray();
        _streamLevels = e.StreamLevels.ToArray();
        _mixedLevel = e.MixedLevel;

        if (!_decayTimer.Enabled)
            _decayTimer.Start();

        Invalidate();
    }

    public void Clear()
    {
        _selections = [];
        _streamLevels = [];
        _mixedLevel = default;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _decayTimer.Dispose();

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using Font labelFont = new(Font.FontFamily, 8f, FontStyle.Regular);
        using Brush textBrush = new SolidBrush(Color.Gainsboro);

        Rectangle content = ClientRectangle;
        content.Inflate(-10, -8);

        if (_selections.Count == 0)
        {
            TextRenderer.DrawText(
                g,
                "Select audio streams and press Play mix",
                Font,
                content,
                Color.Gainsboro,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int meterCount = _selections.Count + 1;
        int gap = 8;
        int labelHeight = 20;
        int titleHeight = 16;
        int availableWidth = Math.Max(1, content.Width - gap * (meterCount - 1));
        int cellWidth = Math.Max(22, availableWidth / meterCount);
        int meterWidth = Math.Min(34, Math.Max(16, cellWidth - 10));
        int meterHeight = Math.Max(32, content.Height - labelHeight - titleHeight - 8);
        int startX = content.Left;
        int meterTop = content.Top + titleHeight;

        for (int i = 0; i < _selections.Count; i++)
        {
            int cellX = startX + i * (cellWidth + gap);
            Rectangle meterBounds = new(
                cellX + (cellWidth - meterWidth) / 2,
                meterTop,
                meterWidth,
                meterHeight);

            StereoLevel level = i < _streamLevels.Length ? _streamLevels[i] : default;
            DrawStereoMeter(g, meterBounds, level, false);
            DrawCenteredText(g, labelFont, textBrush, $"S{_selections[i].StreamIndex}", new Rectangle(cellX, meterBounds.Bottom + 4, cellWidth, labelHeight));
        }

        int mixX = startX + (_selections.Count * (cellWidth + gap));
        Rectangle mixBounds = new(
            mixX + (cellWidth - meterWidth) / 2,
            meterTop,
            meterWidth,
            meterHeight);
        DrawStereoMeter(g, mixBounds, _mixedLevel, true);
        DrawCenteredText(g, labelFont, textBrush, "MIX", new Rectangle(mixX, mixBounds.Bottom + 4, cellWidth, labelHeight));
    }

    private static void DrawStereoMeter(Graphics g, Rectangle bounds, StereoLevel level, bool mixed)
    {
        using Pen borderPen = new(Color.FromArgb(105, 105, 105));
        using Brush backgroundBrush = new SolidBrush(Color.FromArgb(28, 28, 28));
        g.FillRectangle(backgroundBrush, bounds);
        g.DrawRectangle(borderPen, bounds);

        int innerGap = 2;
        int channelWidth = Math.Max(3, (bounds.Width - innerGap - 4) / 2);
        Rectangle leftBounds = new(bounds.Left + 2, bounds.Top + 2, channelWidth, bounds.Height - 4);
        Rectangle rightBounds = new(leftBounds.Right + innerGap, bounds.Top + 2, channelWidth, bounds.Height - 4);

        DrawChannel(g, leftBounds, level.Left, mixed);
        DrawChannel(g, rightBounds, level.Right, mixed);
    }

    private static void DrawChannel(Graphics g, Rectangle bounds, float level, bool mixed)
    {
        level = Math.Clamp(level, 0, 1);
        int fillHeight = (int)Math.Round(bounds.Height * level);
        Rectangle fill = new(bounds.Left, bounds.Bottom - fillHeight, bounds.Width, fillHeight);

        using Brush lowBrush = new SolidBrush(mixed ? Color.FromArgb(0, 174, 221) : Color.FromArgb(67, 178, 92));
        using Brush midBrush = new SolidBrush(Color.FromArgb(230, 180, 52));
        using Brush highBrush = new SolidBrush(Color.FromArgb(224, 80, 80));
        using Brush inactiveBrush = new SolidBrush(Color.FromArgb(38, 38, 38));

        g.FillRectangle(inactiveBrush, bounds);

        if (fill.Height <= 0)
            return;

        int highY = bounds.Top + (int)(bounds.Height * 0.15f);
        int midY = bounds.Top + (int)(bounds.Height * 0.35f);

        Rectangle low = Rectangle.Intersect(fill, new Rectangle(bounds.Left, midY, bounds.Width, bounds.Bottom - midY));
        Rectangle mid = Rectangle.Intersect(fill, new Rectangle(bounds.Left, highY, bounds.Width, midY - highY));
        Rectangle high = Rectangle.Intersect(fill, new Rectangle(bounds.Left, bounds.Top, bounds.Width, highY - bounds.Top));

        if (low.Height > 0)
            g.FillRectangle(lowBrush, low);
        if (mid.Height > 0)
            g.FillRectangle(midBrush, mid);
        if (high.Height > 0)
            g.FillRectangle(highBrush, high);
    }

    private static void DrawCenteredText(Graphics g, Font font, Brush brush, string text, Rectangle bounds)
    {
        StringFormat format = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };

        g.DrawString(text, font, brush, bounds, format);
    }

    private void DecayLevels()
    {
        if (_streamLevels.Length == 0 && _mixedLevel == default)
            return;

        const float decay = 0.88f;
        const float threshold = 0.001f;

        for (int i = 0; i < _streamLevels.Length; i++)
            _streamLevels[i] = new StereoLevel(_streamLevels[i].Left * decay, _streamLevels[i].Right * decay);

        _mixedLevel = new StereoLevel(_mixedLevel.Left * decay, _mixedLevel.Right * decay);

        bool allSilent = _mixedLevel.Peak < threshold &&
                         _streamLevels.All(l => l.Peak < threshold);
        if (allSilent)
        {
            _streamLevels = new StereoLevel[_streamLevels.Length];
            _mixedLevel = default;
            _decayTimer.Stop();
        }

        Invalidate();
    }
}

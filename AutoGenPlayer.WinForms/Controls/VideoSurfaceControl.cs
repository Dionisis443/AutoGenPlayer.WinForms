using AutoGen_Player.Video;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AutoGen_Player.Controls;

internal sealed class VideoSurfaceControl : Control
{
    private readonly object _pendingFrameRoot = new();
    private Bitmap? _currentFrame;
    private VideoFrameEventArgs? _pendingFrame;
    private double _currentDisplayAspectRatio;
    private int _frameGeneration;
    private int _pendingFrameGeneration;
    private bool _renderPosted;
    private string _message = "No media loaded";

    public VideoSurfaceControl()
    {
        DoubleBuffered = true;
        BackColor = Color.Black;
        ForeColor = Color.Gainsboro;
    }

    public event EventHandler<TimeSpan>? FramePresented;

    public void SetMessage(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetMessage(message)));
            return;
        }

        _message = message;
        Invalidate();
    }

    public void ShowFrame(VideoFrameEventArgs frame)
    {
        int expectedGeneration = Volatile.Read(ref _frameGeneration);
        if (InvokeRequired)
        {
            EnqueueFrame(frame, expectedGeneration);
            return;
        }

        ShowFrameOnUi(frame, expectedGeneration);
    }

    public void ClearFrame()
    {
        Interlocked.Increment(ref _frameGeneration);
        ClearPendingFrame();

        if (InvokeRequired)
        {
            BeginInvoke(new Action(ClearFrameOnUi));
            return;
        }

        ClearFrameOnUi();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearPendingFrame();
            _currentFrame?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Color.Black);

        if (_currentFrame is null)
        {
            TextRenderer.DrawText(
                e.Graphics,
                _message,
                Font,
                ClientRectangle,
                ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        Rectangle destination = GetFitRectangle(_currentDisplayAspectRatio, ClientRectangle);
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        e.Graphics.DrawImage(_currentFrame, destination);
    }

    private void EnqueueFrame(VideoFrameEventArgs frame, int expectedGeneration)
    {
        if (IsDisposed)
        {
            frame.Dispose();
            return;
        }

        bool postRender = false;
        lock (_pendingFrameRoot)
        {
            if (expectedGeneration != _frameGeneration)
            {
                frame.Dispose();
                return;
            }

            _pendingFrame?.Dispose();
            _pendingFrame = frame;
            _pendingFrameGeneration = expectedGeneration;

            if (!_renderPosted)
            {
                _renderPosted = true;
                postRender = true;
            }
        }

        if (!postRender)
            return;

        try
        {
            BeginInvoke(new Action(RenderPendingFrame));
        }
        catch
        {
            ClearPendingFrame();
        }
    }

    private void RenderPendingFrame()
    {
        VideoFrameEventArgs? frame;
        int expectedGeneration;
        lock (_pendingFrameRoot)
        {
            frame = _pendingFrame;
            expectedGeneration = _pendingFrameGeneration;
            _pendingFrame = null;
            _renderPosted = false;
        }

        if (frame is not null)
            ShowFrameOnUi(frame, expectedGeneration);

        bool postRender = false;
        lock (_pendingFrameRoot)
        {
            if (_pendingFrame is not null && !_renderPosted)
            {
                _renderPosted = true;
                postRender = true;
            }
        }

        if (!postRender || IsDisposed)
            return;

        try
        {
            BeginInvoke(new Action(RenderPendingFrame));
        }
        catch
        {
            ClearPendingFrame();
        }
    }

    private void ShowFrameOnUi(VideoFrameEventArgs frame, int expectedGeneration)
    {
        if (expectedGeneration != _frameGeneration || IsDisposed)
        {
            frame.Dispose();
            return;
        }

        try
        {
            if (_currentFrame is null ||
                _currentFrame.Width != frame.Width ||
                _currentFrame.Height != frame.Height)
            {
                _currentFrame?.Dispose();
                _currentFrame = new Bitmap(frame.Width, frame.Height, PixelFormat.Format24bppRgb);
            }

            BitmapData data = _currentFrame.LockBits(
                new Rectangle(0, 0, frame.Width, frame.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb);

            try
            {
                int sourceStride = frame.Width * 3;
                for (int y = 0; y < frame.Height; y++)
                {
                    IntPtr destination = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(frame.Bgr24Buffer, y * sourceStride, destination, sourceStride);
                }
            }
            finally
            {
                _currentFrame.UnlockBits(data);
            }

            _currentDisplayAspectRatio = frame.DisplayAspectRatio > 0
                ? frame.DisplayAspectRatio
                : frame.Width / (double)frame.Height;
            Invalidate();
            FramePresented?.Invoke(this, frame.Timestamp);
        }
        finally
        {
            frame.Dispose();
        }
    }

    private void ClearFrameOnUi()
    {
        _currentFrame?.Dispose();
        _currentFrame = null;
        _currentDisplayAspectRatio = 0;
        Invalidate();
    }

    private void ClearPendingFrame()
    {
        lock (_pendingFrameRoot)
        {
            _pendingFrame?.Dispose();
            _pendingFrame = null;
            _renderPosted = false;
        }
    }

    private static Rectangle GetFitRectangle(double displayAspectRatio, Rectangle bounds)
    {
        if (displayAspectRatio <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return Rectangle.Empty;

        double boundsAspectRatio = bounds.Width / (double)bounds.Height;
        int width;
        int height;

        if (boundsAspectRatio > displayAspectRatio)
        {
            height = bounds.Height;
            width = Math.Max(1, (int)Math.Round(height * displayAspectRatio));
        }
        else
        {
            width = bounds.Width;
            height = Math.Max(1, (int)Math.Round(width / displayAspectRatio));
        }

        return new Rectangle(
            bounds.Left + (bounds.Width - width) / 2,
            bounds.Top + (bounds.Height - height) / 2,
            width,
            height);
    }
}

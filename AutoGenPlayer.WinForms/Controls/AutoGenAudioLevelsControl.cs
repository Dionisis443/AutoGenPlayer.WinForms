using AutoGen_Player.Audio;

namespace AutoGen_Player.Controls;

public sealed class AutoGenAudioLevelsControl : UserControl
{
    private readonly AudioLevelMeterPanel _meterPanel = new();
    private AutoGenPlayerControl? _player;

    public AutoGenAudioLevelsControl()
    {
        BackColor = Color.FromArgb(18, 18, 18);
        _meterPanel.Dock = DockStyle.Fill;
        Controls.Add(_meterPanel);
    }

    public AutoGenPlayerControl? Player
    {
        get => _player;
        set => Attach(value);
    }

    public void Attach(AutoGenPlayerControl? player)
    {
        if (ReferenceEquals(_player, player))
            return;

        if (_player is not null)
        {
            _player.AudioLevelsAvailable -= player_AudioLevelsAvailable;
            _player.PlaybackStateChanged -= player_PlaybackStateChanged;
        }

        _player = player;
        Clear();

        if (_player is not null)
        {
            _player.AudioLevelsAvailable += player_AudioLevelsAvailable;
            _player.PlaybackStateChanged += player_PlaybackStateChanged;
        }
    }

    public void UpdateLevels(AudioLevelSnapshotEventArgs levels)
    {
        _meterPanel.UpdateLevels(levels);
    }

    public void Clear()
    {
        _meterPanel.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Attach(null);

        base.Dispose(disposing);
    }

    private void player_AudioLevelsAvailable(object? sender, AudioLevelSnapshotEventArgs e)
    {
        UpdateLevels(e);
    }

    private void player_PlaybackStateChanged(object? sender, EventArgs e)
    {
        if (_player?.SourceFile is null)
            Clear();
    }
}

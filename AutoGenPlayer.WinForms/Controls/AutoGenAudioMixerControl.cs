using AutoGen_Player.Core;
using System.Globalization;

namespace AutoGen_Player.Controls;

public sealed class AutoGenAudioMixerControl : UserControl
{
    private readonly DataGridView _audioGrid = new();
    private readonly AutoGenAudioLevelsControl _audioLevelsControl = new();
    private readonly CheckBox _enableCheckBox = new();
    private readonly NumericUpDown _advanceNumeric = new();
    private readonly Label _statusLabel = new();
    private AutoGenPlayerControl? _player;
    private bool _loading;

    public event EventHandler<bool>? MixerEnabledChanged;

    public AutoGenAudioMixerControl()
    {
        BuildUi();
    }

    public AutoGenPlayerControl? Player
    {
        get => _player;
        set => Attach(value);
    }

    public bool MixerEnabled
    {
        get => _enableCheckBox.Checked;
        set => SetMixerEnabled(value);
    }

    public int AudioAdvanceMilliseconds
    {
        get => (int)_advanceNumeric.Value;
        set => _advanceNumeric.Value = Math.Clamp(value, (int)_advanceNumeric.Minimum, (int)_advanceNumeric.Maximum);
    }

    public IReadOnlyList<AudioMixSelection> CurrentSelections => BuildSelections();

    public void Attach(AutoGenPlayerControl? player)
    {
        if (ReferenceEquals(_player, player))
            return;

        if (_player is not null)
        {
            _player.MediaOpened -= player_MediaOpened;
            _player.PlaybackStateChanged -= player_PlaybackStateChanged;
        }

        _player = player;
        _audioLevelsControl.Attach(_player);

        if (_player is not null)
        {
            _player.MediaOpened += player_MediaOpened;
            _player.PlaybackStateChanged += player_PlaybackStateChanged;
        }

        LoadAudioGrid();
        SetEnableCheckedSilently(_player?.PipelineMode == PlayerPipelineMode.AutoGenMixer);
    }

    public void ReloadStreams()
    {
        LoadAudioGrid();
    }

    public void ApplyMix(bool restartIfPlaying)
    {
        if (_loading || _player?.SourceFile is null)
            return;

        try
        {
            IReadOnlyList<AudioMixSelection> selections = BuildSelections();
            if (selections.Count == 0)
            {
                _player.ClearAudioMix();
                _audioLevelsControl.Clear();
                _statusLabel.Text = MixerEnabled
                    ? "Audio mixer enabled: no streams selected."
                    : "Select at least one audio stream.";
                return;
            }

            bool wasPlaying = _player.PlaybackState == PlayerPlaybackState.Playing;
            TimeSpan currentPosition = _player.CurrentPosition;

            _player.SetAudioMix(selections, TimeSpan.FromMilliseconds(AudioAdvanceMilliseconds));

            if (MixerEnabled && restartIfPlaying && wasPlaying)
                _player.Seek(currentPosition, resume: true);

            _statusLabel.Text = $"Audio mix: {selections.Count} stream(s)";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = ex.Message;
        }
    }

    public void Clear()
    {
        SetEnableCheckedSilently(false);
        _audioGrid.Rows.Clear();
        _audioLevelsControl.Clear();
        _statusLabel.Text = "Ready";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Attach(null);

        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        BackColor = Color.FromArgb(28, 28, 28);

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(8),
            BackColor = Color.FromArgb(28, 28, 28)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        FlowLayoutPanel topPanel = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.FromArgb(28, 28, 28)
        };

        _enableCheckBox.Text = "Enable/Disable";
        _enableCheckBox.ForeColor = Color.WhiteSmoke;
        _enableCheckBox.AutoSize = true;
        _enableCheckBox.Margin = new Padding(0, 7, 18, 0);
        _enableCheckBox.CheckedChanged += enableCheckBox_CheckedChanged;

        Label advanceLabel = new()
        {
            Text = "Advance ms",
            ForeColor = Color.WhiteSmoke,
            AutoSize = true,
            Margin = new Padding(0, 7, 8, 0)
        };

        _advanceNumeric.Minimum = -200;
        _advanceNumeric.Maximum = 200;
        _advanceNumeric.Increment = 1;
        _advanceNumeric.Value = 0;
        _advanceNumeric.Width = 80;
        _advanceNumeric.BackColor = Color.FromArgb(45, 45, 45);
        _advanceNumeric.ForeColor = Color.WhiteSmoke;
        _advanceNumeric.ValueChanged += (_, _) => ApplyMix(restartIfPlaying: true);

        topPanel.Controls.Add(_enableCheckBox);
        topPanel.Controls.Add(advanceLabel);
        topPanel.Controls.Add(_advanceNumeric);

        ConfigureGrid();

        _audioLevelsControl.Dock = DockStyle.Fill;

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.ForeColor = Color.Gainsboro;
        _statusLabel.Text = "Ready";
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        root.Controls.Add(topPanel, 0, 0);
        root.Controls.Add(_audioGrid, 0, 1);
        root.Controls.Add(_audioLevelsControl, 0, 2);
        root.Controls.Add(_statusLabel, 0, 3);

        Controls.Add(root);
    }

    private void ConfigureGrid()
    {
        _audioGrid.Dock = DockStyle.Fill;
        _audioGrid.AllowUserToAddRows = false;
        _audioGrid.AllowUserToDeleteRows = false;
        _audioGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _audioGrid.BackgroundColor = Color.FromArgb(28, 28, 28);
        _audioGrid.BorderStyle = BorderStyle.FixedSingle;
        _audioGrid.EnableHeadersVisualStyles = false;
        _audioGrid.GridColor = Color.FromArgb(80, 80, 80);
        _audioGrid.RowHeadersVisible = false;
        _audioGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _audioGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        _audioGrid.DefaultCellStyle.BackColor = Color.FromArgb(42, 42, 42);
        _audioGrid.DefaultCellStyle.ForeColor = Color.WhiteSmoke;
        _audioGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 122, 204);
        _audioGrid.DefaultCellStyle.SelectionForeColor = Color.White;
        _audioGrid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(50, 50, 50);
        _audioGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(58, 58, 58);
        _audioGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.WhiteSmoke;

        _audioGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Selected",
            HeaderText = "Use",
            FillWeight = 35
        });
        _audioGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Stream",
            HeaderText = "Stream",
            ReadOnly = true,
            FillWeight = 55
        });
        _audioGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Info",
            HeaderText = "Info",
            ReadOnly = true,
            FillWeight = 220
        });
        _audioGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Volume",
            HeaderText = "Volume %",
            FillWeight = 65
        });

        _audioGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_audioGrid.IsCurrentCellDirty && IsCurrentColumn("Selected"))
                _audioGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _audioGrid.CellValueChanged += audioGrid_CellValueChanged;
        _audioGrid.CellEndEdit += audioGrid_CellEndEdit;
        _audioGrid.CellValidating += audioGrid_CellValidating;
        _audioGrid.EditingControlShowing += audioGrid_EditingControlShowing;
        _audioGrid.MouseWheel += audioGrid_MouseWheel;
    }

    private void LoadAudioGrid()
    {
        _loading = true;
        _audioGrid.Rows.Clear();

        if (_player is not null)
        {
            foreach (MediaStreamInfo stream in _player.AudioStreams)
            {
                int rowIndex = _audioGrid.Rows.Add(true, stream.StreamIndex.ToString(CultureInfo.InvariantCulture), stream.DisplayText, "100");
                _audioGrid.Rows[rowIndex].Tag = stream;
            }
        }

        _loading = false;

        if (_player is null || _player.AudioStreams.Count == 0)
        {
            SetEnableCheckedSilently(false);
            _audioLevelsControl.Clear();
            MixerEnabledChanged?.Invoke(this, false);
            _statusLabel.Text = _player?.SourceFile is null ? "Open media first." : "No audio streams found.";
        }
        else
        {
            ApplyMix(restartIfPlaying: false);
        }
    }

    private IReadOnlyList<AudioMixSelection> BuildSelections()
    {
        _audioGrid.EndEdit();
        List<AudioMixSelection> selections = [];

        foreach (DataGridViewRow row in _audioGrid.Rows)
        {
            if (row.Tag is not MediaStreamInfo stream)
                continue;

            bool selected = row.Cells["Selected"].Value is bool value && value;
            if (!selected)
                continue;

            string volumeText = row.Cells["Volume"].Value?.ToString() ?? "100";
            if (!int.TryParse(volumeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int volumePercent))
                volumePercent = 100;

            selections.Add(new AudioMixSelection(
                stream.AudioOrdinal,
                stream.StreamIndex,
                Math.Clamp(volumePercent, 0, 400)));
        }

        return selections;
    }

    private void SetMixerEnabled(bool enabled)
    {
        if (_enableCheckBox.Checked == enabled)
            return;

        _enableCheckBox.Checked = enabled;
    }

    private void enableCheckBox_CheckedChanged(object? sender, EventArgs e)
    {
        if (_loading)
            return;

        try
        {
            if (_player?.SourceFile is null)
            {
                SetEnableCheckedSilently(false);
                _statusLabel.Text = "Open media first.";
                return;
            }

            TimeSpan currentPosition = _player.CurrentPosition;
            if (_enableCheckBox.Checked)
            {
                if (BuildSelections().Count == 0)
                {
                    SetEnableCheckedSilently(false);
                    _statusLabel.Text = "Select at least one audio stream.";
                    return;
                }

                ApplyMix(restartIfPlaying: false);
                _player.SetPlaybackSpeed(1.0);
                _player.PipelineMode = PlayerPipelineMode.AutoGenMixer;
                _player.Seek(currentPosition, resume: true);
                _statusLabel.Text = "Audio mixer enabled";
            }
            else
            {
                _audioLevelsControl.Clear();
                _player.SetPlaybackSpeed(1.0);
                _player.PipelineMode = PlayerPipelineMode.AutoGenSingleStreamAv;
                _player.Seek(currentPosition, resume: true);
                _statusLabel.Text = "Audio mixer disabled";
            }

            MixerEnabledChanged?.Invoke(this, _enableCheckBox.Checked);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = ex.Message;
        }
    }

    private void SetEnableCheckedSilently(bool enabled)
    {
        _loading = true;
        _enableCheckBox.Checked = enabled;
        _loading = false;
    }

    private void audioGrid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loading || e.RowIndex < 0)
            return;

        string columnName = _audioGrid.Columns[e.ColumnIndex].Name;
        if (columnName == "Selected")
            ApplyMix(restartIfPlaying: true);
    }

    private void audioGrid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loading || e.RowIndex < 0)
            return;

        if (_audioGrid.Columns[e.ColumnIndex].Name == "Volume")
            ApplyMix(restartIfPlaying: true);
    }

    private void audioGrid_CellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (_loading || e.RowIndex < 0 || _audioGrid.Columns[e.ColumnIndex].Name != "Volume")
            return;

        string text = e.FormattedValue?.ToString()?.Trim() ?? "100";
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            value = 100;

        _audioGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = Math.Clamp(value, 0, 400).ToString(CultureInfo.InvariantCulture);
    }

    private void audioGrid_EditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        e.Control.KeyPress -= volumeEditingControl_KeyPress;

        if (IsCurrentColumn("Volume"))
            e.Control.KeyPress += volumeEditingControl_KeyPress;
    }

    private static void volumeEditingControl_KeyPress(object? sender, KeyPressEventArgs e)
    {
        if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
            e.Handled = true;
    }

    private void audioGrid_MouseWheel(object? sender, MouseEventArgs e)
    {
        DataGridView.HitTestInfo hit = _audioGrid.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0 || hit.ColumnIndex < 0)
            return;

        if (_audioGrid.Columns[hit.ColumnIndex].Name != "Volume")
            return;

        if (_audioGrid.IsCurrentCellInEditMode)
            _audioGrid.EndEdit();

        DataGridViewCell cell = _audioGrid.Rows[hit.RowIndex].Cells[hit.ColumnIndex];
        string text = cell.Value?.ToString() ?? "100";
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            value = 100;

        int step = (ModifierKeys & Keys.Shift) == Keys.Shift ? 10 : 1;
        int direction = e.Delta > 0 ? 1 : -1;
        cell.Value = Math.Clamp(value + direction * step, 0, 400).ToString(CultureInfo.InvariantCulture);

        _audioGrid.CurrentCell = cell;
        ApplyMix(restartIfPlaying: true);

        if (e is HandledMouseEventArgs handled)
            handled.Handled = true;
    }

    private bool IsCurrentColumn(string columnName)
    {
        return _audioGrid.CurrentCell is not null &&
               _audioGrid.Columns[_audioGrid.CurrentCell.ColumnIndex].Name == columnName;
    }

    private void player_MediaOpened(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
            BeginInvoke(new Action(LoadAudioGrid));
        else
            LoadAudioGrid();
    }

    private void player_PlaybackStateChanged(object? sender, EventArgs e)
    {
        if (_player?.SourceFile is not null)
            return;

        SetEnableCheckedSilently(false);
        _audioLevelsControl.Clear();
        MixerEnabledChanged?.Invoke(this, false);
        LoadAudioGrid();
    }
}

namespace ForzaHaptics.Tester;

internal sealed class TelemetryInspector : UserControl
{
    private readonly DataGridView _telemetryGrid;
    private readonly DataGridView _outputGrid;
    private readonly TextBox _search;
    private readonly Label _summary;
    private readonly Dictionary<string, DataGridViewRow> _telemetryRows =
        new(StringComparer.OrdinalIgnoreCase);

    public TelemetryInspector(IReadOnlyList<TelemetryDescriptor> descriptors)
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(18, 20, 24);

        var header = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(10) };
        _search = new TextBox
        {
            PlaceholderText = Loc.T("Filter telemetry by field or category..."),
            Location = new Point(10, 11),
            Width = 420
        };
        _search.TextChanged += (_, _) => ApplyFilter();
        _summary = new Label
        {
            Text = Loc.T("Waiting for telemetry..."),
            ForeColor = Color.FromArgb(180, 185, 194),
            Location = new Point(450, 14),
            AutoSize = true
        };
        header.Controls.AddRange([_search, _summary]);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 700,
            BackColor = Color.FromArgb(18, 20, 24)
        };

        _telemetryGrid = CreateGrid();
        _telemetryGrid.Columns.Add("Category", Loc.T("Category"));
        _telemetryGrid.Columns.Add("Field", Loc.T("Field"));
        _telemetryGrid.Columns.Add("Key", Loc.T("Key"));
        _telemetryGrid.Columns.Add("Value", Loc.T("Current value"));
        _telemetryGrid.Columns.Add("Unit", Loc.T("Unit"));
        _telemetryGrid.Columns[0].Width = 125;
        _telemetryGrid.Columns[1].Width = 190;
        _telemetryGrid.Columns[2].Width = 225;
        _telemetryGrid.Columns[3].Width = 110;
        _telemetryGrid.Columns[4].Width = 65;

        foreach (var descriptor in descriptors
                     .OrderBy(descriptor => descriptor.Category)
                     .ThenBy(descriptor => descriptor.DisplayName))
        {
            var index = _telemetryGrid.Rows.Add(
                descriptor.Category,
                descriptor.DisplayName,
                descriptor.Key,
                "--",
                descriptor.Unit);
            _telemetryRows[descriptor.Key] = _telemetryGrid.Rows[index];
        }

        var outputPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var outputTitle = new Label
        {
            Text = Loc.T("CURRENT CONTROLLER OUTPUT"),
            Font = new Font("Segoe UI Semibold", 11),
            ForeColor = Color.WhiteSmoke,
            Dock = DockStyle.Top,
            Height = 34
        };
        _outputGrid = CreateGrid();
        _outputGrid.Dock = DockStyle.Top;
        _outputGrid.Height = 230;
        _outputGrid.Columns.Add("Channel", Loc.T("Channel"));
        _outputGrid.Columns.Add("Strength", Loc.T("Strength"));
        _outputGrid.Columns.Add("Frequency", Loc.T("Frequency"));
        _outputGrid.Columns.Add("State", Loc.T("State"));
        _outputGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

        var explanation = new Label
        {
            Text = Loc.T("The values on the left are every field in the official 324-byte FH6 packet plus useful derived signals. The table above shows the outputs currently owned by this application across native Steam Controller actuators and standard Xbox, PlayStation, or 8BitDo rumble motors."),
            ForeColor = Color.FromArgb(170, 175, 185),
            Dock = DockStyle.Fill,
            Padding = new Padding(3, 18, 3, 3)
        };

        outputPanel.Controls.Add(explanation);
        outputPanel.Controls.Add(_outputGrid);
        outputPanel.Controls.Add(outputTitle);
        split.Panel1.Controls.Add(_telemetryGrid);
        split.Panel2.Controls.Add(outputPanel);

        Controls.Add(split);
        Controls.Add(header);
    }

    public void UpdateValues(
        ForzaPacket packet,
        IReadOnlyList<HapticOutputState> hapticOutputs,
        GraphEvaluationResult? graphResult,
        double packetRate)
    {
        foreach (var descriptor in ForzaPacket.AllDescriptors)
        {
            if (!_telemetryRows.TryGetValue(descriptor.Key, out var row))
            {
                continue;
            }

            row.Cells[3].Value = FormatValue(packet.Get(descriptor.Key), descriptor);
        }

        // Zwei ganze Saetze statt "Race " + "active": die Wortstellung ist nicht
        // in jeder Sprache dieselbe.
        _summary.Text = string.Format(
            packet.IsRaceOn
                ? Loc.T("{0} packets/s  ·  Race active  ·  {1} packet fields + {2} derived")
                : Loc.T("{0} packets/s  ·  Race inactive  ·  {1} packet fields + {2} derived"),
            packetRate.ToString("F1"),
            ForzaPacket.Descriptors.Count,
            ForzaPacket.DerivedDescriptors.Count);

        _outputGrid.Rows.Clear();
        foreach (var output in hapticOutputs)
        {
            _outputGrid.Rows.Add(
                output.Name,
                output.Strength.ToString("P1"),
                output.Active
                    ? output.FrequencyHz > 0
                        ? $"{output.FrequencyHz:F0} Hz"
                        : Loc.T("motor intensity")
                    : "--",
                output.Active ? Loc.T("Active") : Loc.T("Off"));
        }

        if (graphResult is not null)
        {
            foreach (var output in graphResult.Outputs.Where(output => output.Strength > 0))
            {
                _outputGrid.Rows.Add(
                    string.Format(Loc.T("Graph: {0}"), output.NodeTitle),
                    output.Strength.ToString("P1"),
                    $"{output.FrequencyHz:F0} Hz",
                    output.EffectMode == HapticEffectMode.Beep ? Loc.T("Pulsed") : Loc.T("Continuous"));
            }
        }
    }

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();
        foreach (DataGridViewRow row in _telemetryGrid.Rows)
        {
            var text = string.Join(
                " ",
                row.Cells.Cast<DataGridViewCell>().Select(cell => cell.Value?.ToString()));
            row.Visible = query.Length == 0 ||
                          text.Contains(query, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FormatValue(double value, TelemetryDescriptor descriptor)
    {
        if (descriptor.Type is TelemetryValueType.Signed32 or
            TelemetryValueType.Unsigned32 or
            TelemetryValueType.Unsigned16 or
            TelemetryValueType.Unsigned8 or
            TelemetryValueType.Signed8)
        {
            return value.ToString("0");
        }

        if (Math.Abs(value) >= 10000)
        {
            return value.ToString("N0");
        }

        return value.ToString("0.000");
    }

    private static DataGridView CreateGrid() =>
        new()
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            BackgroundColor = Color.FromArgb(20, 23, 28),
            ForeColor = Color.WhiteSmoke,
            GridColor = Color.FromArgb(55, 60, 70),
            BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(43, 48, 58),
                ForeColor = Color.WhiteSmoke,
                SelectionBackColor = Color.FromArgb(43, 48, 58)
            },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(27, 30, 36),
                ForeColor = Color.WhiteSmoke,
                SelectionBackColor = Color.FromArgb(50, 91, 150),
                SelectionForeColor = Color.White
            }
        };
}

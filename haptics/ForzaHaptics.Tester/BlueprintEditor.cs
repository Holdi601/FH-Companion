using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace ForzaHaptics.Tester;

internal sealed class BlueprintEditor : UserControl
{
    private readonly SignalGraph _graph;
    private readonly IReadOnlyList<TelemetryDescriptor> _descriptors;
    private readonly Func<IReadOnlyList<ControllerOutputTarget>> _controllerTargets;
    private readonly Func<string> _selectedControllerId;
    private readonly BlueprintCanvas _canvas = null!;
    private readonly Panel _palette;
    private readonly Panel _properties;
    private readonly Panel _scrollHost;
    private readonly SplitContainer _outerSplit;
    private readonly SplitContainer _innerSplit;
    private readonly CheckBox _enabled;
    private readonly Label _zoomLabel;

    public BlueprintEditor(
        SignalGraph graph,
        IReadOnlyList<TelemetryDescriptor> descriptors,
        Func<IReadOnlyList<ControllerOutputTarget>>? controllerTargets = null,
        Func<string>? selectedControllerId = null)
    {
        _graph = graph;
        _descriptors = descriptors;
        _controllerTargets = controllerTargets ?? (() =>
        [
            new ControllerOutputTarget(
                OutputSignalNode.SteamNativeTargetId,
                Loc.T("Steam Controller · native four-channel haptics"),
                true,
                false,
                true,
                false)
        ]);
        _selectedControllerId = selectedControllerId ??
                                (() => OutputSignalNode.SteamNativeTargetId);
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(18, 20, 24);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(5, 4, 5, 3),
            BackColor = Color.FromArgb(28, 31, 37)
        };

        _palette = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(23, 26, 31)
        };

        var paletteTitle = new Label
        {
            Text = Loc.T("NODE PALETTE"),
            Font = new Font("Segoe UI Semibold", 11),
            ForeColor = Color.WhiteSmoke,
            Dock = DockStyle.Top,
            Height = 34
        };

        _enabled = new CheckBox
        {
            Text = Loc.T("Graph output enabled"),
            ForeColor = Color.WhiteSmoke,
            Dock = DockStyle.Top,
            Height = 38,
            Checked = graph.Enabled
        };
        _enabled.CheckedChanged += (_, _) =>
        {
            _graph.Enabled = _enabled.Checked;
            if (!_setzeSchalter)
            {
                OutputEnabledChanged?.Invoke(_enabled.Checked);
            }
        };

        var addConstant = PaletteButton(Loc.T("Add constant test source"));
        addConstant.Click += (_, _) => AddNode(new ConstantSignalNode(
            Loc.T("Constant test"),
            NextLocation(),
            0.25));
        var addTelemetry = PaletteButton(Loc.T("Add telemetry source"));
        addTelemetry.Click += (_, _) => AddTelemetryNode();
        var addPreset = PaletteButton(Loc.T("Add wheel preset"));
        addPreset.Click += (_, _) => ShowWheelPresetMenu(addPreset);
        var addGroup = PaletteButton(Loc.T("Add custom group"));
        addGroup.Click += (_, _) => AddNode(new GroupSignalNode(Loc.T("Signal group"), NextLocation()));
        var addCurve = PaletteButton(Loc.T("Add Bézier curve"));
        addCurve.Click += (_, _) => AddNode(new CurveSignalNode(Loc.T("Response curve"), NextLocation()));
        var addOutput = PaletteButton(Loc.T("Add controller output"));
        addOutput.Click += (_, _) => AddNode(CreateOutputForSelectedController());
        var resetDefault = PaletteButton(Loc.T("Load grip preset"));
        resetDefault.Click += (_, _) => LoadDefaultGraph();
        var save = PaletteButton(Loc.T("Save graph profile"));
        save.Click += (_, _) => SaveGraph();
        var load = PaletteButton(Loc.T("Load graph profile"));
        load.Click += (_, _) => LoadGraph();
        var clear = PaletteButton(Loc.T("Clear graph"));
        clear.Click += (_, _) =>
        {
            _graph.Nodes.Clear();
            _graph.Connections.Clear();
            _canvas.SelectedNode = null;
            _canvas.Invalidate();
            RefreshCanvasExtent();
            ShowProperties(null);
        };

        var hint = new Label
        {
            Text = Loc.T("Drag from a node's right port to another node's left port.\n\nDrag headers to move nodes. Right-click a node to delete it."),
            ForeColor = Color.FromArgb(165, 170, 180),
            Dock = DockStyle.Fill,
            Padding = new Padding(2, 15, 2, 2)
        };

        _palette.Controls.Add(hint);
        _palette.Controls.Add(clear);
        _palette.Controls.Add(load);
        _palette.Controls.Add(save);
        _palette.Controls.Add(resetDefault);
        _palette.Controls.Add(addOutput);
        _palette.Controls.Add(addCurve);
        _palette.Controls.Add(addGroup);
        _palette.Controls.Add(addPreset);
        _palette.Controls.Add(addTelemetry);
        _palette.Controls.Add(addConstant);
        _palette.Controls.Add(_enabled);
        _palette.Controls.Add(paletteTitle);

        _properties = new Panel
        {
            Dock = DockStyle.Right,
            Width = 285,
            Padding = new Padding(12),
            BackColor = Color.FromArgb(23, 26, 31),
            AutoScroll = true
        };

        _scrollHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.FromArgb(15, 17, 21)
        };
        _canvas = new BlueprintCanvas(_graph)
        {
            Size = new Size(1500, 900)
        };
        _canvas.NodeSelected += (_, node) => ShowProperties(node);
        _canvas.WorkspaceChanged += (_, _) => RefreshCanvasExtent();
        _scrollHost.Controls.Add(_canvas);
        _scrollHost.Resize += (_, _) => RefreshCanvasExtent();

        _innerSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2,
            SplitterWidth = 6
        };
        _innerSplit.Panel1.Controls.Add(_scrollHost);
        _innerSplit.Panel2.Controls.Add(_properties);
        _innerSplit.SizeChanged += (_, _) =>
        {
            if (!_innerSplit.Panel2Collapsed && _innerSplit.Width > 520)
            {
                _innerSplit.SplitterDistance = Math.Clamp(
                    _innerSplit.Width - 290,
                    _innerSplit.Panel1MinSize,
                    _innerSplit.Width - _innerSplit.Panel2MinSize - _innerSplit.SplitterWidth);
            }
        };

        _outerSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 6
        };
        _outerSplit.Panel1.Controls.Add(_palette);
        _outerSplit.Panel2.Controls.Add(_innerSplit);
        var togglePalette = ToolbarButton(Loc.T("Palette"));
        togglePalette.Click += (_, _) => _outerSplit.Panel1Collapsed = !_outerSplit.Panel1Collapsed;
        var toggleProperties = ToolbarButton(Loc.T("Properties"));
        toggleProperties.Click += (_, _) => _innerSplit.Panel2Collapsed = !_innerSplit.Panel2Collapsed;
        var focus = ToolbarButton(Loc.T("Focus canvas"));
        focus.Click += (_, _) =>
        {
            var enteringFocus = !_outerSplit.Panel1Collapsed || !_innerSplit.Panel2Collapsed;
            _outerSplit.Panel1Collapsed = enteringFocus;
            _innerSplit.Panel2Collapsed = enteringFocus;
            focus.Text = enteringFocus ? Loc.T("Show panels") : Loc.T("Focus canvas");
            RefreshCanvasExtent();
        };
        var fit = ToolbarButton(Loc.T("Fit nodes"));
        fit.Click += (_, _) => FitNodes();
        var zoomOut = ToolbarButton("−");
        zoomOut.Width = 36;
        zoomOut.Click += (_, _) => SetZoom(_canvas.Zoom - 0.1f);
        _zoomLabel = new Label
        {
            Text = "100%",
            ForeColor = Color.WhiteSmoke,
            TextAlign = ContentAlignment.MiddleCenter,
            Width = 58,
            Height = 30,
            Margin = new Padding(2, 0, 2, 0)
        };
        var zoomIn = ToolbarButton("+");
        zoomIn.Width = 36;
        zoomIn.Click += (_, _) => SetZoom(_canvas.Zoom + 0.1f);
        var expand = ToolbarButton(Loc.T("Expand workspace"));
        expand.Click += (_, _) =>
        {
            _canvas.MinimumWorldSize = new Size(
                _canvas.MinimumWorldSize.Width + 500,
                _canvas.MinimumWorldSize.Height + 350);
            RefreshCanvasExtent();
        };
        toolbar.Controls.AddRange([
            togglePalette,
            toggleProperties,
            focus,
            fit,
            zoomOut,
            _zoomLabel,
            zoomIn,
            expand
        ]);

        Controls.Add(_outerSplit);
        Controls.Add(toolbar);
        ShowProperties(null);
        SetZoom(1f);
        Load += (_, _) =>
        {
            if (_outerSplit.Width > 500)
            {
                _outerSplit.SplitterDistance = Math.Min(190, _outerSplit.Width / 3);
            }
        };
    }

    public void SetLiveValues(IReadOnlyDictionary<Guid, double> values)
    {
        _canvas.LiveValues = values;
        _canvas.Invalidate();
        if (_canvas.SelectedNode is not null)
        {
            _canvas.SelectedValue = values.GetValueOrDefault(_canvas.SelectedNode.Id);
        }
    }

    internal void SelectNodeForTest(SignalNode node) => ShowProperties(node);

    public void RefreshControllerTargets()
    {
        if (_canvas.SelectedNode is OutputSignalNode output)
        {
            ShowProperties(output);
        }
    }

    /// <summary>Der Nutzer hat "Graph output enabled" umgeschaltet (nicht der Code).</summary>
    public event Action<bool>? OutputEnabledChanged;

    private bool _setzeSchalter;

    /// <summary>Ausgabe an oder aus, ohne dass es als Umschalten des Nutzers zaehlt.</summary>
    public void SetOutputEnabled(bool an)
    {
        _setzeSchalter = true;
        try
        {
            _graph.Enabled = an;
            _enabled.Checked = an;
        }
        finally
        {
            _setzeSchalter = false;
        }
    }

    public void SetActiveController(ControllerOutputTarget target)
    {
        foreach (var output in _graph.Nodes.OfType<OutputSignalNode>())
        {
            output.TargetId = target.Id;
            // DEN KANAL NUR ERSETZEN, WENN ES IHN DORT NICHT GIBT. Bis 2026-09-26 bekam
            // hier JEDER Ausgang denselben Kanal -- bei jeder Controllerwahl, also auch
            // beim Start. Links und rechts landeten auf einem Griff, und ein Graph mit
            // DualSense-Abzugskanaelen verlor sie beim Einstecken.
            if (!KanalGibtEs(target, output.Channel))
            {
                output.Channel = target.IsSteamNative
                    ? SteamControllerHaptics.LeftGrip
                    : target.IsDualSenseNative
                        ? DualSenseHaptics.FrequencyMix
                        : GenericGamepadHaptics.FrequencyMix;
            }
        }

        RefreshControllerTargets();
        _canvas.Invalidate();
    }

    internal static bool KanalGibtEs(ControllerOutputTarget target, int kanal) =>
        target.IsSteamNative
            ? kanal is >= SteamControllerHaptics.RightGrip and <= SteamControllerHaptics.RightPad
            : target.IsDualSenseNative
                ? kanal is >= DualSenseHaptics.LowBodyMotor and <= DualSenseHaptics.FrequencyMix
                    or >= DualSenseHaptics.LeftAdaptiveTrigger and <= DualSenseHaptics.BothAdaptiveTriggers
                : kanal is >= GenericGamepadHaptics.LowMotor and <= GenericGamepadHaptics.BothTriggers;

    internal decimal[] GetPropertyNumberValuesForTest() =>
        Descendants(_properties)
            .OfType<NumericUpDown>()
            .Select(number => number.Value)
            .ToArray();

    internal string[] GetPropertyComboItemsForTest() =>
        Descendants(_properties)
            .OfType<ComboBox>()
            .SelectMany(combo => combo.Items.Cast<object>())
            .Select(item => item.ToString() ?? string.Empty)
            .ToArray();

    internal OutputSignalNode CreateOutputForSelectedControllerForTest() =>
        CreateOutputForSelectedController();

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static Button PaletteButton(string text) =>
        new()
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = 42,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(42, 47, 56),
            UseVisualStyleBackColor = false
        };

    private static Button ToolbarButton(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.WhiteSmoke,
            BackColor = Color.FromArgb(43, 48, 58),
            UseVisualStyleBackColor = false,
            Margin = new Padding(2, 0, 2, 0)
        };

    private void SetZoom(float zoom)
    {
        _canvas.Zoom = Math.Clamp(zoom, 0.35f, 2.5f);
        _zoomLabel.Text = $"{_canvas.Zoom:P0}";
        RefreshCanvasExtent();
    }

    private void FitNodes()
    {
        var bounds = _canvas.GetWorldBounds();
        var availableWidth = Math.Max(200, _scrollHost.ClientSize.Width - 40);
        var availableHeight = Math.Max(150, _scrollHost.ClientSize.Height - 40);
        var zoom = Math.Min(
            availableWidth / (float)Math.Max(1, bounds.Width),
            availableHeight / (float)Math.Max(1, bounds.Height));
        SetZoom(Math.Clamp(zoom, 0.35f, 1.75f));
        _scrollHost.AutoScrollPosition = new Point(
            Math.Max(0, (int)(bounds.Left * _canvas.Zoom) - 20),
            Math.Max(0, (int)(bounds.Top * _canvas.Zoom) - 20));
    }

    private void RefreshCanvasExtent()
    {
        if (_scrollHost.ClientSize.Width <= 0 || _scrollHost.ClientSize.Height <= 0)
        {
            return;
        }

        var worldBounds = _canvas.GetWorldBounds();
        var worldWidth = Math.Max(
            _canvas.MinimumWorldSize.Width,
            Math.Max(
                worldBounds.Right + 250,
                (int)Math.Ceiling(_scrollHost.ClientSize.Width / _canvas.Zoom)));
        var worldHeight = Math.Max(
            _canvas.MinimumWorldSize.Height,
            Math.Max(
                worldBounds.Bottom + 220,
                (int)Math.Ceiling(_scrollHost.ClientSize.Height / _canvas.Zoom)));
        _canvas.Size = new Size(
            Math.Max(1, (int)Math.Ceiling(worldWidth * _canvas.Zoom)),
            Math.Max(1, (int)Math.Ceiling(worldHeight * _canvas.Zoom)));
        _canvas.Invalidate();
    }

    private void AddTelemetryNode()
    {
        using var dialog = new TelemetryPickerDialog(_descriptors);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Selected is null)
        {
            return;
        }

        var descriptor = dialog.Selected;
        AddNode(new TelemetrySignalNode(
            descriptor.DisplayName,
            NextLocation(),
            descriptor.Key,
            descriptor.DefaultMinimum,
            descriptor.DefaultMaximum));
    }

    private OutputSignalNode CreateOutputForSelectedController()
    {
        var targetId = _selectedControllerId();
        var target = _controllerTargets().FirstOrDefault(choice => choice.Id == targetId);
        var output = new OutputSignalNode(
            Loc.T("Controller output"),
            NextLocation(),
            target?.IsSteamNative == true
                ? SteamControllerHaptics.LeftGrip
                : target?.IsDualSenseNative == true
                    ? DualSenseHaptics.FrequencyMix
                    : GenericGamepadHaptics.FrequencyMix,
            HapticEffectMode.Rumble,
            70)
        {
            TargetId = targetId
        };
        return output;
    }

    private void ShowWheelPresetMenu(Control owner)
    {
        var menu = new ContextMenuStrip();
        AddPresetItem(menu, Loc.T("Left wheels - grip"), "Derived.GripLeft");
        AddPresetItem(menu, Loc.T("Right wheels - grip"), "Derived.GripRight");
        AddPresetItem(menu, Loc.T("Front wheels - grip"), "Derived.GripFront");
        AddPresetItem(menu, Loc.T("Rear wheels - grip"), "Derived.GripRear");
        menu.Items.Add(new ToolStripSeparator());
        AddPresetItem(menu, Loc.T("Left wheels - lock"), "Derived.LockLeft");
        AddPresetItem(menu, Loc.T("Right wheels - lock"), "Derived.LockRight");
        AddPresetItem(menu, Loc.T("Front wheels - lock"), "Derived.LockFront");
        AddPresetItem(menu, Loc.T("Rear wheels - lock"), "Derived.LockRear");
        menu.Show(owner, new Point(owner.Width, 0));
    }

    private void AddPresetItem(ContextMenuStrip menu, string title, string key)
    {
        var item = menu.Items.Add(title);
        item.Click += (_, _) => AddNode(new TelemetrySignalNode(title, NextLocation(), key, 0, 1));
    }

    private void AddNode(SignalNode node)
    {
        _graph.Nodes.Add(node);
        _canvas.SelectedNode = node;
        _canvas.Invalidate();
        RefreshCanvasExtent();
        ShowProperties(node);
    }

    private Point NextLocation()
    {
        var count = _graph.Nodes.Count;
        return new Point(45 + count % 3 * 260, 55 + count / 3 * 145);
    }

    private void LoadDefaultGraph()
    {
        var replacement = SignalGraph.CreateDefault();
        _graph.Nodes.Clear();
        _graph.Connections.Clear();
        _graph.Nodes.AddRange(replacement.Nodes);
        _graph.Connections.AddRange(replacement.Connections);
        _canvas.SelectedNode = null;
        _canvas.Invalidate();
        RefreshCanvasExtent();
        ShowProperties(null);
    }

    /// <summary>Wo der Pfad der zuletzt benutzten Datei liegt.</summary>
    /// <remarks>
    /// Neben den uebrigen Benutzerdaten, nicht im Programmordner: das Paket wird bei
    /// jedem Bau ersetzt, und eine Merkdatei darin waere nach jedem Update weg.
    /// </remarks>
    private static string LastGraphPointer => Path.Combine(AppInfo.DataFolder, "last_blueprint.txt");

    private static void RememberGraph(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastGraphPointer)!);
            File.WriteAllText(LastGraphPointer, path);
        }
        catch (Exception)
        {
            // Sich den Pfad nicht merken zu koennen ist ein Komfortverlust, kein Grund,
            // das Speichern des Graphen selbst scheitern zu lassen.
        }
    }

    /// <summary>
    /// Den zuletzt benutzten Graphen laden, wenn es einen gibt.
    /// </summary>
    /// <remarks>
    /// Wird beim Oeffnen des Reiters gerufen. Schlaegt es fehl -- Datei verschoben,
    /// geloescht, unlesbar -- bleibt der eingebaute Standardgraph stehen und es
    /// passiert NICHTS weiter: ein leerer Editor waere schlimmer als der Standard, und
    /// eine Fehlermeldung beim Programmstart waere es auch.
    /// </remarks>
    public void LoadLastGraph()
    {
        try
        {
            if (!File.Exists(LastGraphPointer)) { return; }
            var pfad = File.ReadAllText(LastGraphPointer).Trim();
            if (pfad.Length == 0 || !File.Exists(pfad)) { return; }
            ApplyLoadedGraph(pfad);
        }
        catch (Exception)
        {
            // siehe oben
        }
    }

    private void ApplyLoadedGraph(string path)
    {
        // Laden schaltet die Ausgabe weder an noch aus: ob der Graph den Controller
        // treibt, entscheidet der Schalter (gemerkt in haptics_graph_enabled), nicht
        // die Datei. Wer die Ausgabe aus hat, bekommt sie durch ein Profil nicht an.
        var an = _graph.Enabled;
        SignalGraphPersistence.LoadInto(_graph, path);
        SetOutputEnabled(an);
        _canvas.SelectedNode = null;
        _canvas.Invalidate();
        RefreshCanvasExtent();
        ShowProperties(null);
    }

    private void SaveGraph()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = Loc.T("Forza haptic graph") + " (*.fhgraph.json)|*.fhgraph.json|JSON (*.json)|*.json",
            FileName = "forza-haptics.fhgraph.json"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            SignalGraphPersistence.Save(_graph, dialog.FileName);
            // Auch das Speichern macht eine Datei zur zuletzt benutzten -- wer einen
            // Graphen ablegt, arbeitet an ihm weiter.
            RememberGraph(dialog.FileName);
        }
    }

    private void LoadGraph()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = Loc.T("Forza haptic graph") + " (*.fhgraph.json)|*.fhgraph.json|JSON (*.json)|*.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        ApplyLoadedGraph(dialog.FileName);
        RememberGraph(dialog.FileName);
    }

    private void ShowProperties(SignalNode? node)
    {
        _properties.SuspendLayout();
        _properties.Controls.Clear();

        var title = new Label
        {
            Text = node is null ? Loc.T("NODE PROPERTIES") : TypName(node.Type),
            Font = new Font("Segoe UI Semibold", 11),
            ForeColor = Color.WhiteSmoke,
            Dock = DockStyle.Top,
            Height = 34
        };
        _properties.Controls.Add(title);

        if (node is null)
        {
            _properties.Controls.Add(new Label
            {
                Text = Loc.T("Select a node to edit it."),
                ForeColor = Color.FromArgb(165, 170, 180),
                Dock = DockStyle.Top,
                Height = 40
            });
            _properties.ResumeLayout();
            return;
        }

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Top,
            Width = 250
        };
        _properties.Controls.Add(stack);
        title.BringToFront();

        var nodeTitle = TextProperty(stack, Loc.T("Name"), node.Title);
        nodeTitle.TextChanged += (_, _) =>
        {
            node.Title = nodeTitle.Text;
            _canvas.Invalidate();
        };

        switch (node)
        {
            case ConstantSignalNode constant:
                var constantValue = NumberProperty(
                    stack,
                    Loc.T("Constant output (%)"),
                    constant.Value * 100,
                    0,
                    100,
                    1);
                constantValue.ValueChanged += (_, _) =>
                    constant.Value = (double)constantValue.Value / 100.0;
                var constantActive = CheckProperty(stack, Loc.T("Test source active"), constant.Active);
                constantActive.CheckedChanged += (_, _) => constant.Active = constantActive.Checked;
                stack.Controls.Add(new Label
                {
                    Text = Loc.T("Connect this directly to an output node to verify one controller actuator at an exact force."),
                    ForeColor = Color.FromArgb(242, 162, 96),
                    MinimumSize = new Size(245, 58),
                    MaximumSize = new Size(245, 0),
                    AutoSize = true
                });
                break;
            case TelemetrySignalNode telemetry:
                BuildTelemetryProperties(stack, telemetry);
                break;
            case GroupSignalNode group:
                var groupMode = ComboProperty(
                    stack,
                    Loc.T("Combine inputs"),
                    Enum.GetValues<SignalGroupMode>(),
                    group.Mode,
                    GruppenModus);
                groupMode.SelectedValueChanged += (_, _) =>
                    group.Mode = (SignalGroupMode)groupMode.SelectedItem!;
                break;
            case CurveSignalNode curve:
                var editCurve = ActionButton(Loc.T("Edit multi-point Bézier curve"));
                editCurve.Click += (_, _) => EditCurve(curve);
                stack.Controls.Add(editCurve);
                break;
            case OutputSignalNode output:
                BuildOutputProperties(stack, output);
                break;
        }

        var delete = ActionButton(Loc.T("Delete selected node"));
        delete.BackColor = Color.FromArgb(105, 54, 58);
        delete.Click += (_, _) =>
        {
            _graph.RemoveNode(node.Id);
            _canvas.SelectedNode = null;
            _canvas.Invalidate();
            ShowProperties(null);
        };
        stack.Controls.Add(delete);
        _properties.ResumeLayout();
    }

    private void BuildTelemetryProperties(FlowLayoutPanel stack, TelemetrySignalNode node)
    {
        LabelProperty(stack, Loc.T("Telemetry field"));
        var fields = new ComboBox
        {
            Width = 245,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(TelemetryDescriptor.DisplayName)
        };
        fields.Items.AddRange(_descriptors.Cast<object>().ToArray());
        fields.SelectedItem = _descriptors.FirstOrDefault(descriptor => descriptor.Key == node.TelemetryKey);
        fields.SelectedValueChanged += (_, _) =>
        {
            if (fields.SelectedItem is not TelemetryDescriptor descriptor)
            {
                return;
            }

            node.TelemetryKey = descriptor.Key;
            node.Minimum = descriptor.DefaultMinimum;
            node.Maximum = descriptor.DefaultMaximum;
            ShowProperties(node);
            _canvas.Invalidate();
        };
        stack.Controls.Add(fields);

        var minimum = NumberProperty(stack, Loc.T("Normalize minimum"), node.Minimum, -1000000, 1000000, 3);
        minimum.ValueChanged += (_, _) => node.Minimum = (double)minimum.Value;
        var maximum = NumberProperty(stack, Loc.T("Normalize maximum"), node.Maximum, -1000000, 1000000, 3);
        maximum.ValueChanged += (_, _) => node.Maximum = (double)maximum.Value;
        var absolute = CheckProperty(stack, Loc.T("Absolute value"), node.Absolute);
        absolute.CheckedChanged += (_, _) => node.Absolute = absolute.Checked;
        var invert = CheckProperty(stack, Loc.T("Invert 0 ↔ 1"), node.Invert);
        invert.CheckedChanged += (_, _) => node.Invert = invert.Checked;
    }

    private void BuildOutputProperties(FlowLayoutPanel stack, OutputSignalNode node)
    {
        LabelProperty(stack, Loc.T("Controller target"));
        var targets = _controllerTargets().ToList();
        if (targets.All(target => target.Id != node.TargetId))
        {
            targets.Add(new ControllerOutputTarget(
                node.TargetId,
                string.Format(Loc.T("Unavailable saved controller ({0})"), node.TargetId),
                node.TargetId == OutputSignalNode.SteamNativeTargetId,
                node.TargetId.StartsWith("dualsense:", StringComparison.Ordinal),
                true,
                false));
        }

        var target = new ComboBox
        {
            Width = 245,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(ControllerOutputTarget.Name)
        };
        target.Items.AddRange(targets.Cast<object>().ToArray());
        target.SelectedItem = targets.First(choice => choice.Id == node.TargetId);
        target.SelectedValueChanged += (_, _) =>
        {
            var selected = (ControllerOutputTarget)target.SelectedItem!;
            node.TargetId = selected.Id;
            node.Channel = selected.IsSteamNative
                ? SteamControllerHaptics.LeftGrip
                : selected.IsDualSenseNative
                    ? DualSenseHaptics.FrequencyMix
                    : GenericGamepadHaptics.FrequencyMix;
            ShowProperties(node);
            _canvas.Invalidate();
        };
        stack.Controls.Add(target);

        var selectedTarget = targets.First(choice => choice.Id == node.TargetId);
        var adaptiveTrigger = selectedTarget.IsDualSenseNative &&
                              node.Channel is
                                  DualSenseHaptics.LeftAdaptiveTrigger or
                                  DualSenseHaptics.RightAdaptiveTrigger or
                                  DualSenseHaptics.BothAdaptiveTriggers;
        var channelNames = selectedTarget.IsSteamNative
            ? new[]
            {
                new ChannelChoice(SteamControllerHaptics.LeftGrip, Loc.T("Left grip motor")),
                new ChannelChoice(SteamControllerHaptics.RightGrip, Loc.T("Right grip motor")),
                new ChannelChoice(SteamControllerHaptics.LeftPad, Loc.T("Left trackpad")),
                new ChannelChoice(SteamControllerHaptics.RightPad, Loc.T("Right trackpad"))
            }
            : selectedTarget.IsDualSenseNative
                ? BuildDualSenseChannelChoices()
                : BuildGenericChannelChoices(selectedTarget);
        if (channelNames.All(choice => choice.Channel != node.Channel))
        {
            node.Channel = channelNames[0].Channel;
        }

        LabelProperty(stack, Loc.T("Controller channel"));
        var channel = new ComboBox
        {
            Width = 245,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(ChannelChoice.Name)
        };
        channel.Items.AddRange(channelNames);
        channel.SelectedItem = channelNames.First(choice => choice.Channel == node.Channel);
        channel.SelectedValueChanged += (_, _) =>
        {
            node.Channel = ((ChannelChoice)channel.SelectedItem!).Channel;
            ShowProperties(node);
            _canvas.Invalidate();
        };
        stack.Controls.Add(channel);

        if (adaptiveTrigger)
        {
            BuildDualSenseTriggerProperties(stack, node);
        }
        else if (!selectedTarget.IsSteamNative)
        {
            stack.Controls.Add(new Label
            {
                Text = Loc.T("Standard controllers expose low/high motor intensity, not a literal carrier frequency. Select Frequency mix to make frequency modulation crossfade between those motors. Trigger rumble is normally available only on compatible Xbox controllers."),
                ForeColor = Color.FromArgb(242, 162, 96),
                MinimumSize = new Size(245, 106),
                MaximumSize = new Size(245, 0),
                AutoSize = true
            });
        }

        LabelProperty(stack, Loc.T("Multiple input connections"));
        var inputModeChoices = new[]
        {
            new InputModeChoice(SignalGroupMode.Maximum, GruppenModus(SignalGroupMode.Maximum)),
            new InputModeChoice(SignalGroupMode.Minimum, GruppenModus(SignalGroupMode.Minimum)),
            new InputModeChoice(SignalGroupMode.Average, GruppenModus(SignalGroupMode.Average)),
            new InputModeChoice(SignalGroupMode.SumClamped, GruppenModus(SignalGroupMode.SumClamped))
        };
        var inputMode = new ComboBox
        {
            Width = 245,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(InputModeChoice.Name)
        };
        inputMode.Items.AddRange(inputModeChoices);
        inputMode.SelectedItem =
            inputModeChoices.First(choice => choice.Mode == node.InputMode);
        inputMode.SelectedValueChanged += (_, _) =>
            node.InputMode = ((InputModeChoice)inputMode.SelectedItem!).Mode;
        stack.Controls.Add(inputMode);
        stack.Controls.Add(new Label
        {
            Text = Loc.T("Maximum uses the strongest input. Average blends them; SumClamped adds them up to 100%. If several output nodes target the same actuator, the strongest live output wins."),
            ForeColor = Color.FromArgb(165, 170, 180),
            MinimumSize = new Size(245, 78),
            MaximumSize = new Size(245, 0),
            AutoSize = true
        });

        LabelProperty(stack, Loc.T("Effect"));
        EffectChoice[] effectChoices = adaptiveTrigger
            ?
            [
                new EffectChoice(HapticEffectMode.Rumble, Loc.T("Continuous trigger effect")),
                new EffectChoice(HapticEffectMode.Beep, Loc.T("Pulsed trigger effect"))
            ]
            :
            [
                new EffectChoice(HapticEffectMode.Rumble, Loc.T("Continuous tone / vibration")),
                new EffectChoice(HapticEffectMode.Beep, Loc.T("Pulsed beep"))
            ];
        var effect = new ComboBox
        {
            Width = 245,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(EffectChoice.Name)
        };
        effect.Items.AddRange(effectChoices);
        effect.SelectedItem = effectChoices.First(choice => choice.Mode == node.EffectMode);
        effect.SelectedValueChanged += (_, _) =>
        {
            node.EffectMode = ((EffectChoice)effect.SelectedItem!).Mode;
            ShowProperties(node);
            _canvas.Invalidate();
        };
        stack.Controls.Add(effect);

        LabelProperty(stack, Loc.T("Input modulates"));
        var frequencyRelevant =
            !adaptiveTrigger ||
            node.DualSenseTriggerEffect == DualSenseTriggerEffectMode.Vibration;
        if (!frequencyRelevant)
        {
            node.ModulationMode = HapticModulationMode.StrengthOnly;
        }

        ModulationChoice[] modulationChoices = frequencyRelevant
            ?
            [
                new ModulationChoice(HapticModulationMode.StrengthOnly, Loc.T("Strength only")),
                new ModulationChoice(HapticModulationMode.FrequencyOnly, Loc.T("Frequency only")),
                new ModulationChoice(
                    HapticModulationMode.StrengthAndFrequency,
                    Loc.T("Strength + frequency"))
            ]
            :
            [
                new ModulationChoice(HapticModulationMode.StrengthOnly, Loc.T("Resistance strength"))
            ];
        var modulation = new ComboBox
        {
            Width = 245,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(ModulationChoice.Name)
        };
        modulation.Items.AddRange(modulationChoices);
        modulation.SelectedItem =
            modulationChoices.First(choice => choice.Mode == node.ModulationMode);
        modulation.SelectedValueChanged += (_, _) =>
        {
            node.ModulationMode = ((ModulationChoice)modulation.SelectedItem!).Mode;
            ShowProperties(node);
            _canvas.Invalidate();
        };
        stack.Controls.Add(modulation);

        if (node.ModulationMode == HapticModulationMode.FrequencyOnly)
        {
            var strength = NumberProperty(
                stack,
                Loc.T("Fixed strength / volume (%)"),
                node.MaximumStrength * 100,
                0,
                100,
                0);
            strength.ValueChanged += (_, _) =>
                node.MaximumStrength = (double)strength.Value / 100.0;
        }
        else
        {
            var minimumStrength = NumberProperty(
                stack,
                Loc.T("Strength at 0% input (%)"),
                node.MinimumStrength * 100,
                0,
                100,
                0);
            minimumStrength.ValueChanged += (_, _) =>
                node.MinimumStrength = (double)minimumStrength.Value / 100.0;
            var maximumStrength = NumberProperty(
                stack,
                Loc.T("Strength at 100% input (%)"),
                node.MaximumStrength * 100,
                0,
                100,
                0);
            maximumStrength.ValueChanged += (_, _) =>
                node.MaximumStrength = (double)maximumStrength.Value / 100.0;
        }

        if (frequencyRelevant &&
            node.ModulationMode == HapticModulationMode.StrengthOnly)
        {
            var frequency = NumberProperty(
                stack,
                Loc.T("Fixed carrier frequency / pitch (Hz)"),
                node.MinimumFrequencyHz,
                adaptiveTrigger ? 1 : 20,
                adaptiveTrigger ? 255 : 800,
                0);
            frequency.ValueChanged += (_, _) =>
            {
                node.MinimumFrequencyHz = (double)frequency.Value;
                node.MaximumFrequencyHz = node.MinimumFrequencyHz;
                _canvas.Invalidate();
            };
        }
        else if (frequencyRelevant)
        {
            var minimumFrequency = NumberProperty(
                stack,
                Loc.T("Frequency at 0% input (Hz)"),
                node.MinimumFrequencyHz,
                adaptiveTrigger ? 1 : 20,
                adaptiveTrigger ? 255 : 800,
                0);
            minimumFrequency.ValueChanged += (_, _) =>
            {
                node.MinimumFrequencyHz = (double)minimumFrequency.Value;
                _canvas.Invalidate();
            };
            var maximumFrequency = NumberProperty(
                stack,
                Loc.T("Frequency at 100% input (Hz)"),
                node.MaximumFrequencyHz,
                adaptiveTrigger ? 1 : 20,
                adaptiveTrigger ? 255 : 800,
                0);
            maximumFrequency.ValueChanged += (_, _) =>
            {
                node.MaximumFrequencyHz = (double)maximumFrequency.Value;
                _canvas.Invalidate();
            };
        }

        var silenceAtZero = CheckProperty(
            stack,
            Loc.T("Silence when input is exactly 0%"),
            node.SilenceAtZero);
        silenceAtZero.CheckedChanged += (_, _) =>
            node.SilenceAtZero = silenceAtZero.Checked;

        if (node.EffectMode == HapticEffectMode.Beep)
        {
            var beepRate = NumberProperty(
                stack,
                Loc.T("Pulse rate (pulses/second)"),
                node.BeepRateHz,
                0.1m,
                30,
                1);
            beepRate.ValueChanged += (_, _) => node.BeepRateHz = (double)beepRate.Value;
            var dutyCycle = NumberProperty(
                stack,
                Loc.T("Pulse on-time (%)"),
                node.BeepDutyCycle * 100,
                5,
                95,
                0);
            dutyCycle.ValueChanged += (_, _) =>
                node.BeepDutyCycle = (double)dutyCycle.Value / 100.0;
            stack.Controls.Add(new Label
            {
                Text = adaptiveTrigger
                    ? Loc.T("Pulse rate repeatedly enables and releases the selected L2/R2 effect.")
                    : Loc.T("Pulse rate is the Morse-like rhythm. For an unbroken audible tone, choose Continuous tone / vibration and set the carrier frequency above."),
                ForeColor = Color.FromArgb(242, 162, 96),
                MinimumSize = new Size(245, 72),
                MaximumSize = new Size(245, 0),
                AutoSize = true
            });
        }
    }

    private void BuildDualSenseTriggerProperties(
        FlowLayoutPanel stack,
        OutputSignalNode node)
    {
        var effect = ComboProperty(
            stack,
            Loc.T("Adaptive trigger effect"),
            Enum.GetValues<DualSenseTriggerEffectMode>(),
            node.DualSenseTriggerEffect,
            TriggerEffektName);
        effect.SelectedValueChanged += (_, _) =>
        {
            node.DualSenseTriggerEffect =
                (DualSenseTriggerEffectMode)effect.SelectedItem!;
            ShowProperties(node);
        };

        var start = NumberProperty(
            stack,
            Loc.T("Effect starts at trigger travel (%)"),
            node.TriggerStartPosition * 100,
            0,
            100,
            0);
        start.ValueChanged += (_, _) =>
            node.TriggerStartPosition = (double)start.Value / 100.0;

        if (node.DualSenseTriggerEffect is
            DualSenseTriggerEffectMode.TensionSlope or
            DualSenseTriggerEffectMode.WeaponClick or
            DualSenseTriggerEffectMode.BowSnap)
        {
            var end = NumberProperty(
                stack,
                Loc.T("Effect ends at trigger travel (%)"),
                node.TriggerEndPosition * 100,
                0,
                100,
                0);
            end.ValueChanged += (_, _) =>
                node.TriggerEndPosition = (double)end.Value / 100.0;
        }

        if (node.DualSenseTriggerEffect ==
            DualSenseTriggerEffectMode.TensionSlope)
        {
            var beginningStrength = NumberProperty(
                stack,
                Loc.T("Resistance at slope start (% of output)"),
                node.TriggerSecondaryStrength * 100,
                0,
                100,
                0);
            beginningStrength.ValueChanged += (_, _) =>
                node.TriggerSecondaryStrength =
                    (double)beginningStrength.Value / 100.0;
        }

        if (node.DualSenseTriggerEffect == DualSenseTriggerEffectMode.BowSnap)
        {
            var snap = NumberProperty(
                stack,
                Loc.T("Snap-back strength (% of output)"),
                node.TriggerSnapStrength * 100,
                0,
                100,
                0);
            snap.ValueChanged += (_, _) =>
                node.TriggerSnapStrength = (double)snap.Value / 100.0;
        }

        stack.Controls.Add(new Label
        {
            Text = node.DualSenseTriggerEffect switch
            {
                DualSenseTriggerEffectMode.Resistance =>
                    Loc.T("Adds resistance after the selected trigger position."),
                DualSenseTriggerEffectMode.TensionSlope =>
                    Loc.T("Resistance rises across the selected travel range—useful for brake pressure or bow tension."),
                DualSenseTriggerEffectMode.WeaponClick =>
                    Loc.T("Creates a hard resistance wall followed by a release/click."),
                DualSenseTriggerEffectMode.Vibration =>
                    Loc.T("Vibrates inside L2/R2. Output strength controls amplitude and output frequency controls trigger pulse frequency."),
                DualSenseTriggerEffectMode.BowSnap =>
                    Loc.T("Experimental firmware effect with tension and snap-back. It may vary across controller firmware."),
                _ => string.Empty
            },
            ForeColor = Color.FromArgb(242, 162, 96),
            MinimumSize = new Size(245, 72),
            MaximumSize = new Size(245, 0),
            AutoSize = true
        });
    }

    private void EditCurve(CurveSignalNode curve)
    {
        using var dialog = new Form
        {
            Text = string.Format(Loc.T("Edit curve: {0}"), curve.Title),
            ClientSize = new Size(560, 350),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Color.FromArgb(18, 20, 24),
            ForeColor = Color.WhiteSmoke
        };
        var editor = new BezierCurveEditor
        {
            Location = new Point(20, 20)
        };
        editor.BindCurve(curve.Curve);
        var add = new Button { Text = Loc.T("Add point"), Location = new Point(20, 290), Size = new Size(110, 36) };
        add.Click += (_, _) => editor.AddPoint();
        var remove = new Button { Text = Loc.T("Remove selected"), Location = new Point(140, 290), Size = new Size(140, 36) };
        remove.Click += (_, _) => editor.RemoveSelectedPoint();
        var close = new Button { Text = Loc.T("Done"), Location = new Point(430, 290), Size = new Size(100, 36) };
        close.Click += (_, _) => dialog.Close();
        dialog.Controls.AddRange([editor, add, remove, close]);
        dialog.ShowDialog(this);
        _canvas.Invalidate();
    }

    private static TextBox TextProperty(Control parent, string name, string value)
    {
        LabelProperty(parent, name);
        var box = new TextBox { Text = value, Width = 245 };
        parent.Controls.Add(box);
        return box;
    }

    private static NumericUpDown NumberProperty(
        Control parent,
        string name,
        double value,
        decimal minimum,
        decimal maximum,
        int decimals)
    {
        LabelProperty(parent, name);
        var number = new NumericUpDown
        {
            Width = 245,
            Minimum = minimum,
            Maximum = maximum,
            DecimalPlaces = decimals,
            Increment = decimals == 0 ? 1 : 0.1m,
            Value = Math.Clamp((decimal)value, minimum, maximum)
        };
        parent.Controls.Add(number);
        return number;
    }

    private static ComboBox ComboProperty<T>(
        Control parent,
        string name,
        T[] values,
        T selected,
        Func<T, string>? anzeige = null)
    {
        LabelProperty(parent, name);
        var combo = new ComboBox
        {
            Width = 245,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        if (anzeige is not null)
        {
            // DIE LISTE HAELT DEN WERT, DIE ANZEIGE DEN UEBERSETZTEN NAMEN: SelectedItem
            // bleibt die Aufzaehlung. Erst den Formatierer, dann einschalten, dann die
            // Eintraege -- sonst stehen sie schon mit dem Aufzaehlungsnamen darin.
            combo.Format += (_, e) =>
            {
                if (e.ListItem is T wert)
                {
                    e.Value = anzeige(wert);
                }
            };
            combo.FormattingEnabled = true;
        }

        combo.Items.AddRange(values.Cast<object>().ToArray());
        combo.SelectedItem = selected;
        parent.Controls.Add(combo);
        return combo;
    }

    private static CheckBox CheckProperty(Control parent, string name, bool value)
    {
        var check = new CheckBox
        {
            Text = name,
            Checked = value,
            ForeColor = Color.WhiteSmoke,
            Width = 245,
            Height = 30
        };
        parent.Controls.Add(check);
        return check;
    }

    private static void LabelProperty(Control parent, string text) =>
        parent.Controls.Add(new Label
        {
            Text = text,
            ForeColor = Color.FromArgb(175, 180, 190),
            MinimumSize = new Size(245, 24),
            MaximumSize = new Size(245, 0),
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0)
        });

    private static Button ActionButton(string text) =>
        new()
        {
            Text = text,
            Width = 245,
            Height = 38,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(50, 70, 105),
            Margin = new Padding(0, 12, 0, 0)
        };

    /// <summary>Die Knotenart als Ueberschrift der Eigenschaften.</summary>
    private static string TypName(SignalNodeType type) =>
        type switch
        {
            SignalNodeType.Constant => Loc.T("CONSTANT"),
            SignalNodeType.Telemetry => Loc.T("TELEMETRY"),
            SignalNodeType.Group => Loc.T("GROUP"),
            SignalNodeType.Curve => Loc.T("CURVE"),
            SignalNodeType.Output => Loc.T("OUTPUT"),
            _ => type.ToString().ToUpperInvariant()
        };

    /// <summary>Wie mehrere Eingaenge zusammengehen -- der Name in der Auswahl.</summary>
    private static string GruppenModus(SignalGroupMode mode) =>
        mode switch
        {
            SignalGroupMode.Maximum => Loc.T("Maximum (strongest wins)"),
            SignalGroupMode.Minimum => Loc.T("Minimum"),
            SignalGroupMode.Average => Loc.T("Average"),
            SignalGroupMode.SumClamped => Loc.T("Sum (clamped to 100%)"),
            _ => mode.ToString()
        };

    /// <summary>Der Name eines Abzugseffekts -- die Aufzaehlung bleibt, was gespeichert wird.</summary>
    private static string TriggerEffektName(DualSenseTriggerEffectMode mode) =>
        mode switch
        {
            DualSenseTriggerEffectMode.Resistance => Loc.T("Resistance"),
            DualSenseTriggerEffectMode.TensionSlope => Loc.T("Tension slope"),
            DualSenseTriggerEffectMode.WeaponClick => Loc.T("Weapon click"),
            DualSenseTriggerEffectMode.Vibration => Loc.T("Vibration"),
            DualSenseTriggerEffectMode.BowSnap => Loc.T("Bow snap"),
            _ => mode.ToString()
        };

    private sealed record ChannelChoice(int Channel, string Name);
    private sealed record EffectChoice(HapticEffectMode Mode, string Name);
    private sealed record InputModeChoice(SignalGroupMode Mode, string Name);
    private sealed record ModulationChoice(HapticModulationMode Mode, string Name);

    private static ChannelChoice[] BuildGenericChannelChoices(ControllerOutputTarget target)
    {
        var choices = new List<ChannelChoice>();
        if (target.SupportsMainRumble)
        {
            choices.AddRange([
                new ChannelChoice(GenericGamepadHaptics.LowMotor, Loc.T("Low-frequency body motor")),
                new ChannelChoice(GenericGamepadHaptics.HighMotor, Loc.T("High-frequency body motor")),
                new ChannelChoice(GenericGamepadHaptics.BothMotors, Loc.T("Both body motors")),
                new ChannelChoice(
                    GenericGamepadHaptics.FrequencyMix,
                    Loc.T("Frequency mix (low ↔ high motor)"))
            ]);
        }

        if (target.SupportsTriggerRumble)
        {
            choices.AddRange([
                new ChannelChoice(GenericGamepadHaptics.LeftTrigger, Loc.T("Left trigger motor")),
                new ChannelChoice(GenericGamepadHaptics.RightTrigger, Loc.T("Right trigger motor")),
                new ChannelChoice(GenericGamepadHaptics.BothTriggers, Loc.T("Both trigger motors"))
            ]);
        }

        return choices.Count > 0
            ? choices.ToArray()
            : [new ChannelChoice(GenericGamepadHaptics.BothMotors, Loc.T("Both body motors"))];
    }

    private static ChannelChoice[] BuildDualSenseChannelChoices() =>
    [
        new(DualSenseHaptics.LowBodyMotor, Loc.T("Low-frequency body haptic")),
        new(DualSenseHaptics.HighBodyMotor, Loc.T("High-frequency body haptic")),
        new(DualSenseHaptics.BothBodyMotors, Loc.T("Both body haptics")),
        new(DualSenseHaptics.FrequencyMix, Loc.T("Frequency mix (low ↔ high body)")),
        new(DualSenseHaptics.LeftAdaptiveTrigger, Loc.T("L2 adaptive trigger")),
        new(DualSenseHaptics.RightAdaptiveTrigger, Loc.T("R2 adaptive trigger")),
        new(DualSenseHaptics.BothAdaptiveTriggers, Loc.T("Both adaptive triggers"))
    ];
}

internal sealed class BlueprintCanvas : Control
{
    private static readonly Size NodeSize = new(205, 105);
    private readonly SignalGraph _graph;
    private SignalNode? _dragNode;
    private Point _dragOffset;
    private SignalNode? _connectionSource;
    private Point _connectionPointer;
    private float _zoom = 1f;

    public BlueprintCanvas(SignalGraph graph)
    {
        _graph = graph;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(15, 17, 21);
        ForeColor = Color.WhiteSmoke;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SignalNode? SelectedNode { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double SelectedValue { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyDictionary<Guid, double> LiveValues { get; set; } =
        new Dictionary<Guid, double>();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, 0.35f, 2.5f);
            Invalidate();
        }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Size MinimumWorldSize { get; set; } = new(1500, 900);

    public event EventHandler<SignalNode?>? NodeSelected;
    public event EventHandler? WorkspaceChanged;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.ScaleTransform(Zoom, Zoom);
        DrawGrid(graphics);

        foreach (var connection in _graph.Connections)
        {
            var from = _graph.Nodes.FirstOrDefault(node => node.Id == connection.FromNodeId);
            var to = _graph.Nodes.FirstOrDefault(node => node.Id == connection.ToNodeId);
            if (from is null || to is null)
            {
                continue;
            }

            DrawConnection(graphics, OutputPort(from), InputPort(to), Color.FromArgb(95, 163, 255));
        }

        if (_connectionSource is not null)
        {
            DrawConnection(graphics, OutputPort(_connectionSource), _connectionPointer, Color.FromArgb(245, 190, 75));
        }

        foreach (var node in _graph.Nodes)
        {
            DrawNode(graphics, node);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var worldPoint = ToWorld(e.Location);
        var node = NodeAt(worldPoint);
        if (e.Button == MouseButtons.Right && node is not null)
        {
            var menu = new ContextMenuStrip();
            var delete = menu.Items.Add(Loc.T("Delete node"));
            delete.Click += (_, _) =>
            {
                _graph.RemoveNode(node.Id);
                if (SelectedNode?.Id == node.Id)
                {
                    SelectedNode = null;
                    NodeSelected?.Invoke(this, null);
                }

                Invalidate();
                WorkspaceChanged?.Invoke(this, EventArgs.Empty);
            };
            menu.Show(this, e.Location);
            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var outputNode = _graph.Nodes.FirstOrDefault(candidate =>
            candidate.Type != SignalNodeType.Output &&
            Distance(OutputPort(candidate), worldPoint) <= 13);
        if (outputNode is not null)
        {
            _connectionSource = outputNode;
            _connectionPointer = worldPoint;
            Capture = true;
            return;
        }

        SelectedNode = node;
        NodeSelected?.Invoke(this, node);
        if (node is not null && worldPoint.Y <= node.Location.Y + 30)
        {
            _dragNode = node;
            _dragOffset = new Point(worldPoint.X - node.Location.X, worldPoint.Y - node.Location.Y);
            Capture = true;
        }

        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var worldPoint = ToWorld(e.Location);
        if (_dragNode is not null)
        {
            _dragNode.Location = new Point(
                Math.Max(0, worldPoint.X - _dragOffset.X),
                Math.Max(0, worldPoint.Y - _dragOffset.Y));
            Invalidate();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (_connectionSource is not null)
        {
            _connectionPointer = worldPoint;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var worldPoint = ToWorld(e.Location);
        if (_connectionSource is not null)
        {
            var target = _graph.Nodes.FirstOrDefault(candidate =>
                candidate.Type is not SignalNodeType.Telemetry and not SignalNodeType.Constant &&
                Distance(InputPort(candidate), worldPoint) <= 16);
            if (target is not null)
            {
                _graph.Connect(_connectionSource.Id, target.Id);
            }
        }

        _dragNode = null;
        _connectionSource = null;
        Capture = false;
        Invalidate();
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public Rectangle GetWorldBounds()
    {
        if (_graph.Nodes.Count == 0)
        {
            return new Rectangle(0, 0, 600, 400);
        }

        var left = _graph.Nodes.Min(node => node.Location.X);
        var top = _graph.Nodes.Min(node => node.Location.Y);
        var right = _graph.Nodes.Max(node => node.Location.X + NodeSize.Width);
        var bottom = _graph.Nodes.Max(node => node.Location.Y + NodeSize.Height);
        return Rectangle.FromLTRB(
            Math.Max(0, left - 40),
            Math.Max(0, top - 40),
            right + 40,
            bottom + 40);
    }

    private void DrawNode(Graphics graphics, SignalNode node)
    {
        var rectangle = new Rectangle(node.Location, NodeSize);
        var selected = SelectedNode?.Id == node.Id;
        var headerColor = node.Type switch
        {
            SignalNodeType.Constant => Color.FromArgb(170, 123, 38),
            SignalNodeType.Telemetry => Color.FromArgb(43, 112, 92),
            SignalNodeType.Group => Color.FromArgb(100, 75, 145),
            SignalNodeType.Curve => Color.FromArgb(51, 101, 174),
            SignalNodeType.Output => Color.FromArgb(157, 77, 50),
            _ => Color.Gray
        };

        using var bodyBrush = new SolidBrush(Color.FromArgb(31, 35, 42));
        using var headerBrush = new SolidBrush(headerColor);
        using var outline = new Pen(selected ? Color.FromArgb(255, 190, 70) : Color.FromArgb(80, 87, 100), selected ? 3 : 1);
        graphics.FillRectangle(bodyBrush, rectangle);
        graphics.FillRectangle(headerBrush, rectangle.X, rectangle.Y, rectangle.Width, 31);
        graphics.DrawRectangle(outline, rectangle);

        using var titleFont = new Font("Segoe UI Semibold", 9);
        using var titleBrush = new SolidBrush(Color.White);
        graphics.DrawString(node.Title, titleFont, titleBrush, rectangle.X + 9, rectangle.Y + 7);
        using var detailBrush = new SolidBrush(Color.FromArgb(185, 190, 200));
        // ZWEI ZEILEN PLATZ, im Knoten: eine uebersetzte Zeile ist oft laenger als die
        // englische und lief sonst ueber den rechten Rand hinaus.
        using var detailFormat = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.LineLimit
        };
        graphics.DrawString(
            NodeDetail(node),
            Font,
            detailBrush,
            new RectangleF(rectangle.X + 9, rectangle.Y + 42, rectangle.Width - 18, 32),
            detailFormat);
        graphics.DrawString(
            string.Format(Loc.T("Live: {0}"), LiveValues.GetValueOrDefault(node.Id).ToString("P1")),
            Font,
            titleBrush,
            rectangle.X + 9,
            rectangle.Y + 76);

        if (node.Type is not SignalNodeType.Telemetry and not SignalNodeType.Constant)
        {
            DrawPort(graphics, InputPort(node), Color.FromArgb(245, 190, 75));
        }

        if (node.Type != SignalNodeType.Output)
        {
            DrawPort(graphics, OutputPort(node), Color.FromArgb(95, 163, 255));
        }
    }

    private static string NodeDetail(SignalNode node) =>
        node switch
        {
            ConstantSignalNode constant => constant.Active
                ? string.Format(Loc.T("Constant: {0}"), constant.Value.ToString("P1"))
                : Loc.T("Constant: inactive"),
            TelemetrySignalNode telemetry => telemetry.TelemetryKey,
            GroupSignalNode group => string.Format(Loc.T("Group: {0}"), GruppenKurz(group.Mode)),
            CurveSignalNode curve => string.Format(Loc.T("Bézier: {0} points"), curve.Curve.Nodes.Count),
            OutputSignalNode output =>
                $"{ChannelName(output)} · {EffectName(output.EffectMode)} · {FrequencyDetail(output)}",
            _ => ""
        };

    /// <summary>Der Gruppenmodus kurz, fuer die Zeile im Knoten.</summary>
    private static string GruppenKurz(SignalGroupMode mode) =>
        mode switch
        {
            SignalGroupMode.Maximum => Loc.T("Maximum"),
            SignalGroupMode.Minimum => Loc.T("Minimum"),
            SignalGroupMode.Average => Loc.T("Average"),
            SignalGroupMode.SumClamped => Loc.T("Sum (clamped to 100%)"),
            _ => mode.ToString()
        };

    private static string EffectName(HapticEffectMode mode) =>
        mode == HapticEffectMode.Beep ? Loc.T("Pulsed") : Loc.T("Continuous");

    private static string FrequencyDetail(OutputSignalNode output) =>
        output.ModulationMode == HapticModulationMode.StrengthOnly
            ? $"{output.MinimumFrequencyHz:F0} Hz"
            : $"{output.MinimumFrequencyHz:F0}–{output.MaximumFrequencyHz:F0} Hz";

    private static string ChannelName(OutputSignalNode output)
    {
        if (output.TargetId.StartsWith("dualsense:", StringComparison.Ordinal))
        {
            return output.Channel switch
            {
                DualSenseHaptics.LowBodyMotor => Loc.T("Low body"),
                DualSenseHaptics.HighBodyMotor => Loc.T("High body"),
                DualSenseHaptics.BothBodyMotors => Loc.T("Both body"),
                DualSenseHaptics.FrequencyMix => Loc.T("Body frequency mix"),
                DualSenseHaptics.LeftAdaptiveTrigger => Loc.T("L2 adaptive"),
                DualSenseHaptics.RightAdaptiveTrigger => Loc.T("R2 adaptive"),
                DualSenseHaptics.BothAdaptiveTriggers => Loc.T("L2 + R2 adaptive"),
                _ => "DualSense"
            };
        }

        if (output.TargetId != OutputSignalNode.SteamNativeTargetId)
        {
            return output.Channel switch
            {
                GenericGamepadHaptics.LowMotor => Loc.T("Low motor"),
                GenericGamepadHaptics.HighMotor => Loc.T("High motor"),
                GenericGamepadHaptics.BothMotors => Loc.T("Both motors"),
                GenericGamepadHaptics.FrequencyMix => Loc.T("Frequency mix"),
                GenericGamepadHaptics.LeftTrigger => Loc.T("Left trigger"),
                GenericGamepadHaptics.RightTrigger => Loc.T("Right trigger"),
                GenericGamepadHaptics.BothTriggers => Loc.T("Both triggers"),
                _ => Loc.T("Gamepad")
            };
        }

        return output.Channel switch
        {
            SteamControllerHaptics.LeftGrip => Loc.T("Left grip"),
            SteamControllerHaptics.RightGrip => Loc.T("Right grip"),
            SteamControllerHaptics.LeftPad => Loc.T("Left pad"),
            SteamControllerHaptics.RightPad => Loc.T("Right pad"),
            _ => Loc.T("unknown")
        };
    }

    private SignalNode? NodeAt(Point point) =>
        _graph.Nodes.LastOrDefault(node => new Rectangle(node.Location, NodeSize).Contains(point));

    private static Point InputPort(SignalNode node) =>
        new(node.Location.X, node.Location.Y + NodeSize.Height / 2);

    private static Point OutputPort(SignalNode node) =>
        new(node.Location.X + NodeSize.Width, node.Location.Y + NodeSize.Height / 2);

    private static void DrawConnection(Graphics graphics, Point from, Point to, Color color)
    {
        using var path = new GraphicsPath();
        var distance = Math.Max(55, Math.Abs(to.X - from.X) / 2);
        path.AddBezier(
            from,
            new Point(from.X + distance, from.Y),
            new Point(to.X - distance, to.Y),
            to);
        using var pen = new Pen(color, 3);
        graphics.DrawPath(pen, path);
    }

    private static void DrawPort(Graphics graphics, Point point, Color color)
    {
        using var brush = new SolidBrush(color);
        using var outline = new Pen(Color.FromArgb(15, 17, 21), 2);
        graphics.FillEllipse(brush, point.X - 7, point.Y - 7, 14, 14);
        graphics.DrawEllipse(outline, point.X - 7, point.Y - 7, 14, 14);
    }

    private void DrawGrid(Graphics graphics)
    {
        using var pen = new Pen(Color.FromArgb(29, 32, 38), 1);
        var worldWidth = (int)Math.Ceiling(Width / Zoom);
        var worldHeight = (int)Math.Ceiling(Height / Zoom);
        for (var x = 0; x < worldWidth; x += 24)
        {
            graphics.DrawLine(pen, x, 0, x, worldHeight);
        }

        for (var y = 0; y < worldHeight; y += 24)
        {
            graphics.DrawLine(pen, 0, y, worldWidth, y);
        }
    }

    private Point ToWorld(Point point) =>
        new(
            (int)Math.Round(point.X / Zoom),
            (int)Math.Round(point.Y / Zoom));

    private static double Distance(Point first, Point second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        return Math.Sqrt(x * x + y * y);
    }
}

internal sealed class TelemetryPickerDialog : Form
{
    private readonly ListBox _list;
    private readonly TextBox _search;
    private readonly IReadOnlyList<TelemetryDescriptor> _descriptors;

    public TelemetryPickerDialog(IReadOnlyList<TelemetryDescriptor> descriptors)
    {
        _descriptors = descriptors;
        Text = Loc.T("Choose Forza telemetry");
        ClientSize = new Size(520, 520);
        StartPosition = FormStartPosition.CenterParent;

        _search = new TextBox { Dock = DockStyle.Top, PlaceholderText = Loc.T("Search telemetry...") };
        _search.TextChanged += (_, _) => RefreshList();
        _list = new ListBox { Dock = DockStyle.Fill, DisplayMember = nameof(TelemetryChoice.Label) };
        _list.DoubleClick += (_, _) => Accept();
        var choose = new Button { Text = Loc.T("Add source node"), Dock = DockStyle.Bottom, Height = 45 };
        choose.Click += (_, _) => Accept();

        Controls.Add(_list);
        Controls.Add(_search);
        Controls.Add(choose);
        RefreshList();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TelemetryDescriptor? Selected { get; private set; }

    private void RefreshList()
    {
        var query = _search.Text.Trim();
        _list.Items.Clear();
        _list.Items.AddRange(_descriptors
            .Where(descriptor =>
                query.Length == 0 ||
                descriptor.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                descriptor.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                descriptor.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(descriptor => descriptor.Category)
            .ThenBy(descriptor => descriptor.DisplayName)
            .Select(descriptor => new TelemetryChoice(
                descriptor,
                $"{descriptor.Category}  ·  {descriptor.DisplayName}  [{descriptor.Key}]"))
            .Cast<object>()
            .ToArray());
    }

    private void Accept()
    {
        if (_list.SelectedItem is not TelemetryChoice choice)
        {
            return;
        }

        Selected = choice.Descriptor;
        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed record TelemetryChoice(TelemetryDescriptor Descriptor, string Label);
}

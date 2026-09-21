using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using GunsawImageCode;

namespace GunsawArtConverter;

internal sealed class MainForm : Form
{
    private readonly PreviewBox _preview = new();
    private readonly ThemedNumericUpDown _contourResolution = Number(512, 8, 2048);
    private readonly ThemedNumericUpDown _contourThreshold = Number(5, 1, 100);
    private readonly ThemedNumericUpDown _detail = Number(1, 1, 10);
    private readonly ThemedNumericUpDown _worldWidth = Number(15m, 1m, 200m, 1, 1m);
    private readonly ThemedNumericUpDown _lineThickness = Number(0.05m, 0.01m, 5m, 2, 0.01m);
    private readonly ThemedNumericUpDown _contourSmoothing = Number(0, 0, 3m, 2, 0.1m);
    private readonly ThemedNumericUpDown _minimumBoundaryLength = Number(0, 0, 10000);
    private readonly ThemedNumericUpDown _defectFilter = Number(0, 0, 1000);
    private readonly ThemedComboBox _lineObject = new();
    private readonly ThemedNumericUpDown _curveOptimization = Number(10, 0, 100);
    private readonly Label _status = new();
    private readonly Panel _contourPanel = new();
    private Bitmap? _source;
    private Bitmap? _generatedPreview;
    private ArtDocument? _document;
    private string _generatedCode = string.Empty;

    public MainForm()
    {
        Text = "Gunsaw Art Converter";
        MinimumSize = new Size(900, 820);
        Size = new Size(1120, 850);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(27, 30, 36);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5f);
        AllowDrop = true;

        var sidebar = new Panel
            { Dock = DockStyle.Left, Width = 310, Padding = new Padding(16), BackColor = Color.FromArgb(34, 38, 45) };
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        Controls.Add(right);
        Controls.Add(sidebar);

        var open = Button("Open Image", 44);
        open.Click += (_, _) => OpenImage();
        sidebar.Controls.Add(open);
        BuildContourPanel();
        _contourPanel.SetBounds(16, 70, 278, 560);
        sidebar.Controls.Add(_contourPanel);

        var copy = Button("Copy Code", 44);
        copy.Dock = DockStyle.Bottom;
        copy.Click += (_, _) => CopyCode();
        sidebar.Controls.Add(copy);
        _status.Dock = DockStyle.Bottom;
        _status.Height = 64;
        _status.ForeColor = Color.FromArgb(170, 185, 200);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        sidebar.Controls.Add(_status);

        right.Controls.Add(_preview);

        foreach (var numeric in new[]
                 {
                     _contourResolution, _contourThreshold, _detail, _worldWidth, _lineThickness, _contourSmoothing,
                     _minimumBoundaryLength, _defectFilter, _curveOptimization
                 })
            numeric.ValueChanged += (_, _) => Generate();
        _lineObject.SelectedIndexChanged += (_, _) => Generate();

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _preview.AllowDrop = true;
        _preview.DragEnter += OnDragEnter;
        _preview.DragDrop += OnDragDrop;
        SetStatus("Open a PNG, JPG or BMP. You can also drag an image into this window.");
    }

    private void BuildContourPanel()
    {
        _contourPanel.BackColor = Color.Transparent;
        _contourPanel.Controls.Add(Label("Analysis resolution", 0));
        Place(_contourResolution, 24, _contourPanel);
        _contourPanel.Controls.Add(Label("Color sensitivity", 56));
        Place(_contourThreshold, 78, _contourPanel);
        _contourPanel.Controls.Add(Label("Detail", 112));
        Place(_detail, 134, _contourPanel);
        _contourPanel.Controls.Add(Label("Art width in world units", 168));
        Place(_worldWidth, 190, _contourPanel);
        _contourPanel.Controls.Add(Label("Line thickness", 224));
        Place(_lineThickness, 246, _contourPanel);
        _contourPanel.Controls.Add(Label("Noise smoothing", 280));
        Place(_contourSmoothing, 302, _contourPanel);
        _contourPanel.Controls.Add(Label("Minimum boundary length", 336));
        Place(_minimumBoundaryLength, 358, _contourPanel);
        _contourPanel.Controls.Add(Label("Generation mode / line object", 392));
        FillObjectChoices(_lineObject, 2);
        StylePanelInput(_lineObject, 414);
        _contourPanel.Controls.Add(_lineObject);
        _contourPanel.Controls.Add(Label("Isolated defect filter", 448));
        Place(_defectFilter, 470, _contourPanel);
        _contourPanel.Controls.Add(Label("Curve optimization", 504));
        Place(_curveOptimization, 526, _contourPanel);
    }

    private void OpenImage()
    {
        using var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            LoadImage(dialog.FileName);
    }

    private void LoadImage(string path)
    {
        try
        {
            using var file = new Bitmap(path);
            _source?.Dispose();
            _source = new Bitmap(file);
            Generate();
        }
        catch (Exception ex)
        {
            SetStatus("Could not open the image: " + ex.Message, true);
        }
    }

    private void Generate()
    {
        if (_source == null)
            return;
        try
        {
            _generatedPreview?.Dispose();
            var textColumns = _lineObject.SelectedIndex == 4;
            _document = textColumns
                ? ImageProcessor.TextColumns(_source, (int)_contourResolution.Value, (float)_worldWidth.Value,
                    out _generatedPreview)
                : ImageProcessor.Contours(_source, (int)_contourResolution.Value, (int)_contourThreshold.Value,
                    (int)_detail.Value, (float)_worldWidth.Value, (float)_lineThickness.Value,
                    (float)_contourSmoothing.Value, (int)_minimumBoundaryLength.Value, (int)_defectFilter.Value,
                    SelectedKind(_lineObject), (int)_curveOptimization.Value, out _generatedPreview);

            if (_document.Objects.Count > 50_000)
            {
                _document = null;
                _generatedCode = string.Empty;
                SetStatus("Too many objects (> 50,000). Reduce the resolution.", true);
            }
            else
            {
                _generatedCode = ArtCode.Encode(_document);
                SetStatus(string.Empty);
            }

            _preview.Image = _generatedPreview;
            _preview.Invalidate();
        }
        catch (Exception ex)
        {
            SetStatus("Generation failed: " + ex.Message, true);
        }
    }

    private void CopyCode()
    {
        if (string.IsNullOrEmpty(_generatedCode))
        {
            SetStatus("Open an image first.", true);
            return;
        }

        Clipboard.SetText(_generatedCode);
        SetStatus(string.Empty);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            LoadImage(paths[0]);
    }

    private void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.FromArgb(255, 120, 120) : Color.FromArgb(170, 185, 200);
    }

    private static ThemedNumericUpDown Number(decimal value, decimal min, decimal max, int decimals = 0,
        decimal increment = 1)
        => new()
        {
            Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Increment = increment, Width = 278,
            Height = 30, BackColor = ThemeColors.ControlBack, ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

    private static Label Label(string text, int top) => new()
        { Text = text, Left = 0, Top = top, Width = 278, Height = 22, ForeColor = Color.FromArgb(200, 210, 220) };

    private static Button Button(string text, int height)
    {
        var button = new Button
        {
            Text = text, Dock = DockStyle.Top, Height = height, FlatStyle = FlatStyle.Flat,
            BackColor = ThemeColors.ButtonBack, ForeColor = Color.White
        };
        button.FlatAppearance.BorderColor = ThemeColors.ControlBorder;
        button.FlatAppearance.MouseOverBackColor = ThemeColors.ButtonHover;
        button.FlatAppearance.MouseDownBackColor = ThemeColors.ListSelection;
        return button;
    }

    private static void StylePanelInput(ThemedComboBox box, int top)
    {
        box.Left = 0;
        box.Top = top;
        box.Width = 278;
        box.DropDownStyle = ComboBoxStyle.DropDownList;
        box.BackColor = ThemeColors.ControlBack;
        box.ForeColor = Color.White;
        box.FlatStyle = FlatStyle.Flat;
    }

    private static void FillObjectChoices(ThemedComboBox box, int selectedIndex)
    {
        box.Items.AddRange(new object[] { "Tile", "Background", "Info Screen", "Ice", "Text columns (Info Screen)" });
        box.SelectedIndex = selectedIndex;
    }

    private static ArtObjectKind SelectedKind(ThemedComboBox box)
    {
        return box.SelectedIndex switch
        {
            0 => ArtObjectKind.Tile,
            1 => ArtObjectKind.Background,
            3 => ArtObjectKind.Ice,
            _ => ArtObjectKind.InfoScreen
        };
    }

    private static void Place(Control control, int top, Control parent)
    {
        control.Left = 0;
        control.Top = top;
        parent.Controls.Add(control);
    }
}